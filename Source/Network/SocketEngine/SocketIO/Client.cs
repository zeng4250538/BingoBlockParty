using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketEngine.SocketIO.Helpers;
using SocketEngine.SocketIO.Messages;
using SocketEngine.WebSocket;

namespace SocketEngine.SocketIO
{
	/// <summary>
	/// Class to emulate socket.io javascript client capabilities for .net classes
	/// </summary>
	/// <exception cref = "ArgumentException">Connection for wss or https urls</exception>  
	public class Client : IDisposable, IClient
	{
		private Timer socketHeartBeatTimer; // HeartBeat timer 
		private Task dequeuOutBoundMsgTask;
		private BlockingCollection<string> outboundQueue;
		private int retryConnectionCount = 0;
		private int retryConnectionAttempts = 3;
		private readonly static object padLock = new object(); // allow one connection attempt at a time

		/// <summary>
		/// Uri of Websocket server
		/// </summary>
		protected Uri uri;
		/// <summary>
		/// Underlying WebSocket implementation
		/// </summary>
		protected WebSocket.WebSocket wsClient;
		/// <summary>
		/// RegistrationManager for dynamic events
		/// </summary>
		protected RegistrationManager registrationManager;  // allow registration of dynamic events (event names) for client actions
		/// <summary>
		/// By Default, use WebSocketVersion.Rfc6455
		/// </summary>
		protected WebSocketVersion socketVersion = WebSocketVersion.Rfc6455;

		// Events
		/// <summary>
		/// Opened event comes from the underlying websocket client connection being opened.  This is not the same as socket.io returning the 'connect' event
		/// </summary>
		public event EventHandler Opened;
		public event EventHandler<MessageEventArgs> Message;
		public event EventHandler ConnectionRetryAttempt;
		public event EventHandler HeartBeatTimerEvent;
		/// <summary>
		/// <para>The underlying websocket connection has closed (unexpectedly)</para>
		/// <para>The Socket.IO service may have closed the connection due to a heartbeat timeout, or the connection was just broken</para>
		/// <para>Call the client.Connect() method to re-establish the connection</para>
		/// </summary>
		public event EventHandler SocketConnectionClosed;
		public event EventHandler<ErrorEventArgs> Error;

		/// <summary>
		/// ResetEvent for Outbound MessageQueue Empty Event - all pending messages have been sent
		/// </summary>
		public ManualResetEvent MessageQueueEmptyEvent = new ManualResetEvent(true);

		/// <summary>
		/// Connection Open Event
		/// </summary>
		public ManualResetEvent ConnectionOpenEvent = new ManualResetEvent(false);


		/// <summary>
		/// Number of reconnection attempts before raising SocketConnectionClosed event - (default = 3)
		/// </summary>
		public int RetryConnectionAttempts
		{
			get { return this.retryConnectionAttempts; }
			set { this.retryConnectionAttempts = value; }
		}

		/// <summary>
		/// Value of the last error message text  
		/// </summary>
		public string LastErrorMessage = "";

		/// <summary>
		/// Represents the initial handshake parameters received from the socket.io service (SID, HeartbeatTimeout etc)
		/// </summary>
		public SocketIOHandshake HandShake {get;private set;}
		

		/// <summary>
		/// Returns boolean of ReadyState == WebSocketState.Open
		/// </summary>
		public bool IsConnected
		{
			get
			{
				return this.ReadyState == WebSocketState.Open;
			}
		}

		/// <summary>
		/// Connection state of websocket client: None, Connecting, Open, Closing, Closed
		/// </summary>
		public WebSocketState ReadyState
		{
			get
			{
				if (this.wsClient != null)
					return this.wsClient.State;
				else
					return WebSocketState.None;
			}
		}

		// Constructors
		public Client(string url)
			: this(url, WebSocketVersion.Rfc6455)
		{
		}
		public Client(string url, NameValueCollection headers)
			: this(url, WebSocketVersion.Rfc6455, headers)
		{
		}
		public Client(string url, WebSocketVersion socketVersion)
			: this(url, WebSocketVersion.Rfc6455, null)
		{
		}
		public Client(string url, WebSocketVersion socketVersion, NameValueCollection headers)
		{
			this.uri = new Uri(url);

			this.socketVersion = socketVersion;
			this.HandShake = new SocketIOHandshake(headers);

			this.registrationManager = new RegistrationManager();
			this.outboundQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
			this.dequeuOutBoundMsgTask = Task.Factory.StartNew(() => dequeuOutboundMessages(), TaskCreationOptions.LongRunning);
		}
		/// <summary>
		/// Initiate the connection with Socket.IO service
		/// </summary>
		public void Connect()
		{
			lock (padLock)
			{
				if (!(this.ReadyState == WebSocketState.Connecting || this.ReadyState == WebSocketState.Open))
				{
					try
					{
						this.ConnectionOpenEvent.Reset();
						this.requestHandshake(uri);// perform an initial HTTP request as a new, non-handshaken connection

						if (string.IsNullOrWhiteSpace(this.HandShake.SID) || this.HandShake.HadError)
						{
							this.LastErrorMessage = string.Format("Error initializing handshake with {0}", uri.ToString());
							this.OnErrorEvent(this, new ErrorEventArgs(this.LastErrorMessage, new Exception()));
						}
						else
						{
							string wsScheme = (uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws");
							string host = uri.Host.Contains("localhost") || uri.Host.Contains("127.0.0.1") ? IPAddress.Loopback.ToString() : uri.Host;
							this.wsClient = new WebSocket.WebSocket(
								string.Format("{0}://{1}:{2}/socket.io/1/websocket/{3}", wsScheme, host, uri.Port, this.HandShake.SID),
								string.Empty,
								this.socketVersion);
							this.wsClient.EnableAutoSendPing = false; // #4 tkiley: Websocket4net client library initiates a websocket heartbeat, causes delivery problems
							this.wsClient.Opened += this.wsClient_OpenEvent;
							this.wsClient.MessageReceived += this.wsClient_MessageReceived;
							this.wsClient.Error += this.wsClient_Error;

							this.wsClient.Closed += wsClient_Closed;

							this.wsClient.Open();
						}
					}
					
					catch (Exception ex)
					{
						Debug.WriteLine(string.Format("Connect threw an exception...{0}", ex.Message));
						this.OnErrorEvent(this, new ErrorEventArgs("SocketIO.Client.Connect threw an exception", ex));
					}
				}
			}
		}
		public IEndPointClient Connect(string endPoint)
		{
			EndPointClient nsClient = new EndPointClient(this, endPoint);
			this.Connect();
			this.Send(new ConnectMessage(endPoint));
			return nsClient;
		}

		protected void ReConnect()
		{
			this.retryConnectionCount++;

			this.OnConnectionRetryAttemptEvent(this, EventArgs.Empty);

			this.closeHeartBeatTimer(); // stop the heartbeat time
			this.closeWebSocketClient();// stop websocket
			this.HandShake.ResetConnection();

			this.Connect();

			bool connected = this.ConnectionOpenEvent.WaitOne(4000); // block while waiting for connection
			Debug.WriteLine(string.Format("\tRetry-Connection successful: {0}", connected));
			if (connected)
				this.retryConnectionCount = 0;
			else
			{	// we didn't connect - try again until exhausted
				if (this.retryConnectionCount < this.RetryConnectionAttempts)
				{
					this.ReConnect();
				}
				else
				{
					this.Close();
					this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
				}
			}
		}
		
		/// <summary>
		/// <para>Asynchronously calls the action delegate on event message notification</para>
		/// <para>Mimicks the Socket.IO client 'socket.on('name',function(data){});' pattern</para>
		/// <para>Reserved socket.io event names available: connect, disconnect, open, close, error, retry, reconnect  </para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="action"></param>
		/// <example>
		/// client.On("testme", (data) =>
		///    {
		///        Debug.WriteLine(data.ToJson());
		///    });
		/// </example>
		public virtual void On(
			string eventName,
			Action<IMessage> action)
		{
			this.registrationManager.AddOnEvent(eventName, action);
		}
		public virtual void On(
			string eventName,
			string endPoint,
			Action<IMessage> action)
		{
			
			this.registrationManager.AddOnEvent(eventName, endPoint, action);
		}
		/// <summary>
		/// <para>Asynchronously sends payload using eventName</para>
		/// <para>payload must a string or Json Serializable</para>
		/// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
		/// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="payload">must be a string or a Json Serializable object</param>
		/// <remarks>ArgumentOutOfRangeException will be thrown on reserved event names</remarks>
		public void Emit(string eventName, object payload, string endPoint = "", Action<object> callback = null)
		{

			string lceventName = eventName.ToLower();
			IMessage msg = null;
			switch (lceventName)
			{
				case "message":
					if (payload is string) 
						msg = new TextMessage () { MessageText = payload.ToString() };
					else
						msg = new JSONMessage(payload);
						this.Send(msg);
						break;
				case "connect":
				case "disconnect":
				case "open":
				case "close":
				case "error":
				case "retry":
				case "reconnect":
					throw new System.ArgumentOutOfRangeException(eventName, "Event name is reserved by socket.io, and cannot be used by clients or servers with this message type");
				default:
					if (!string.IsNullOrWhiteSpace(endPoint) && !endPoint.StartsWith("/"))
						endPoint = "/" + endPoint;
					msg = new EventMessage(eventName, payload, endPoint, callback);
					if (callback != null)
						this.registrationManager.AddCallBack(msg);

					this.Send(msg);
					break;
			}
		}

		/// <summary>
		/// <para>Asynchronously sends payload using eventName</para>
		/// <para>payload must a string or Json Serializable</para>
		/// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
		/// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="payload">must be a string or a Json Serializable object</param>
		public void Emit(string eventName, object payload)
		{
			this.Emit(eventName, payload, string.Empty, null);
		}

		/// <summary>
		/// Queue outbound message
		/// </summary>
		/// <param name="msg"></param>
		public void Send(IMessage msg)
		{
			this.MessageQueueEmptyEvent.Reset();
			if (this.outboundQueue != null)
				this.outboundQueue.Add(msg.Encoded);
		}

		private void Send(string rawEncodedMessageText)
		{
			this.MessageQueueEmptyEvent.Reset();
			if (this.outboundQueue != null)
				this.outboundQueue.Add(rawEncodedMessageText);
		}

		/// <summary>
		/// if a registerd event name is found, don't raise the more generic Message event
		/// </summary>
		/// <param name="msg"></param>
		protected void OnMessageEvent(IMessage msg)
		{
			

			bool skip = false;
			if (!string.IsNullOrEmpty(msg.Event))
				skip = this.registrationManager.InvokeOnEvent(msg); // 

			var handler = this.Message;
			if (handler != null && !skip)
			{
				Debug.WriteLine(string.Format("webSocket_OnMessage: {0}", msg.RawMessage));
				handler(this, new MessageEventArgs(msg));
			}
		}
		
		/// <summary>
		/// Close SocketIO4Net.Client and clear all event registrations 
		/// </summary>
		public void Close()
		{
			this.retryConnectionCount = 0; // reset for next connection cycle
			// stop the heartbeat time
			this.closeHeartBeatTimer();

			// stop outbound messages
			this.closeOutboundQueue();

			this.closeWebSocketClient();

			if (this.registrationManager != null)
			{
				this.registrationManager.Dispose();
				this.registrationManager = null;
			}

		}

		protected void closeHeartBeatTimer()
		{
			// stop the heartbeat timer
			if (this.socketHeartBeatTimer != null)
			{
				this.socketHeartBeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
				this.socketHeartBeatTimer.Dispose();
				this.socketHeartBeatTimer = null;
			}
		}
		protected void closeOutboundQueue()
		{
			// stop outbound messages
			if (this.outboundQueue != null)
			{
				this.outboundQueue.CompleteAdding(); // stop adding any more items;
				this.dequeuOutBoundMsgTask.Wait(700); // wait for dequeue thread to stop
				this.outboundQueue.Dispose();
				this.outboundQueue = null;
			}
		}
		protected void closeWebSocketClient()
		{
			if (this.wsClient != null)
			{
				// unwire events
				this.wsClient.Closed -= this.wsClient_Closed;
				this.wsClient.MessageReceived -= wsClient_MessageReceived;
				this.wsClient.Error -= wsClient_Error;
				this.wsClient.Opened -= this.wsClient_OpenEvent;

				if (this.wsClient.State == WebSocketState.Connecting || this.wsClient.State == WebSocketState.Open)
				{
					try { this.wsClient.Close(); }
					catch { Debug.WriteLine("exception raised trying to close websocket: can safely ignore, socket is being closed"); }
				}
				this.wsClient = null;
			}
		}

		// websocket client events - open, messages, errors, closing
		private void wsClient_OpenEvent(object sender, EventArgs e)
		{
			this.socketHeartBeatTimer = new Timer(OnHeartBeatTimerCallback, new object(), HandShake.HeartbeatInterval, HandShake.HeartbeatInterval);
			this.ConnectionOpenEvent.Set();

			this.OnMessageEvent(new EventMessage() { Event = "open" });
			if (this.Opened != null)
			{
				try { this.Opened(this, EventArgs.Empty); }
				catch (Exception ex) { Debug.WriteLine(ex); }
			}

		}

		/// <summary>
		/// Raw websocket messages from server - convert to message types and call subscribers of events and/or callbacks
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void wsClient_MessageReceived(object sender, MessageReceivedEventArgs e)
		{
			

			IMessage iMsg = Messages.Message.Factory(e.Message);

			if (iMsg.Event == "responseMsg")
				Debug.WriteLine(string.Format("InvokeOnEvent: {0}", iMsg.RawMessage));

			switch (iMsg.MessageType)
			{
				case SocketIOMessageTypes.Disconnect:
					this.OnMessageEvent(iMsg);
					if (string.IsNullOrWhiteSpace(iMsg.Endpoint)) // Disconnect the whole socket
						this.Close();
					break;
				case SocketIOMessageTypes.Heartbeat:
					this.OnHeartBeatTimerCallback(null);
					break;
				case SocketIOMessageTypes.Connect:
				case SocketIOMessageTypes.Message:
				case SocketIOMessageTypes.JSONMessage:
				case SocketIOMessageTypes.Event:
				case SocketIOMessageTypes.Error:
					this.OnMessageEvent(iMsg);
					break;
				case SocketIOMessageTypes.ACK:
					this.registrationManager.InvokeCallBack(iMsg.AckId, iMsg.Json);
					break;
				default:
					Debug.WriteLine("unknown wsClient message Received...");
					break;
			}
		}

		/// <summary>
		/// websocket has closed unexpectedly - retry connection
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void wsClient_Closed(object sender, EventArgs e)
		{
			if (this.retryConnectionCount < this.RetryConnectionAttempts   )
			{
				this.ConnectionOpenEvent.Reset();
				this.ReConnect();
			}
			else
			{
				this.Close();
				this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
			}
		}

		private void wsClient_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
		{
			this.OnErrorEvent(sender, new ErrorEventArgs("SocketClient error", e.Exception));
		}

		protected void OnErrorEvent(object sender, ErrorEventArgs e)
		{
			this.LastErrorMessage = e.Message;
			if (this.Error != null)
			{
				try { this.Error.Invoke(this, e); }
				catch { }
			}
			Debug.WriteLine(string.Format("Error Event: {0}\r\n\t{1}", e.Message, e.Exception));
		}
		protected void OnSocketConnectionClosedEvent(object sender, EventArgs e)
		{
			if (this.SocketConnectionClosed != null)
				{
					try { this.SocketConnectionClosed(sender, e); }
					catch { }
				}
			Debug.WriteLine("SocketConnectionClosedEvent");
		}
		protected void OnConnectionRetryAttemptEvent(object sender, EventArgs e)
		{
			if (this.ConnectionRetryAttempt != null)
			{
				try { this.ConnectionRetryAttempt(sender, e); }
				catch (Exception ex) { Debug.WriteLine(ex); }
			}
			Debug.WriteLine(string.Format("Attempting to reconnect: {0}", this.retryConnectionCount));
		}

		// Housekeeping
		protected void OnHeartBeatTimerCallback(object state)
		{
			if (this.ReadyState == WebSocketState.Open)
			{
				IMessage msg = new Heartbeat();
				try
				{
					if (this.outboundQueue != null && !this.outboundQueue.IsAddingCompleted)
					{
						this.outboundQueue.Add(msg.Encoded);
						if (this.HeartBeatTimerEvent != null)
						{
							this.HeartBeatTimerEvent.BeginInvoke(this, EventArgs.Empty, EndAsyncEvent, null);
						}
					}
				}
				catch(Exception ex)
				{
					// 
					Debug.WriteLine(string.Format("OnHeartBeatTimerCallback Error Event: {0}\r\n\t{1}", ex.Message, ex.InnerException));
				}
			}
		}
		private void EndAsyncEvent(IAsyncResult result)
		{
			try
			{
                this.HeartBeatTimerEvent.EndInvoke(result);
			}
			catch
			{
				// Handle any exceptions that were thrown by the invoked method
				Debug.WriteLine("An event listener went kaboom!");
			}
		}
		/// <summary>
		/// While connection is open, dequeue and send messages to the socket server
		/// </summary>
		protected void dequeuOutboundMessages()
		{
			while (this.outboundQueue != null && !this.outboundQueue.IsAddingCompleted)
			{
				if (this.ReadyState == WebSocketState.Open)
				{
					string msgString;
					try
					{
						if (this.outboundQueue.TryTake(out msgString, 500))
						{
							//Trace.WriteLine(string.Format("webSocket_Send: {0}", msgString));
							this.wsClient.Send(msgString);
						}
						else
							this.MessageQueueEmptyEvent.Set();
					}
					catch(Exception ex)
					{
						Debug.WriteLine("The outboundQueue is no longer open...");
					}
				}
				else
				{
					this.ConnectionOpenEvent.WaitOne(2000); // wait for connection event
				}
			}
		}

		/// <summary>
		/// <para>Client performs an initial HTTP POST to obtain a SessionId (sid) assigned to a client, followed
		///  by the heartbeat timeout, connection closing timeout, and the list of supported transports.</para>
		/// <para>The tansport and sid are required as part of the ws: transport connection</para>
		/// </summary>
		/// <param name="uri">http://localhost:3000</param>
		/// <returns>Handshake object with sid value</returns>
		/// <example>DownloadString: 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling</example>
		protected void requestHandshake(Uri uri)
		{
			string value = string.Empty;
			string errorText = string.Empty;

		    WebClient client = new WebClient();
            try 
			{
				try
				{
				    if (this.HandShake.Headers.Count > 0)
				    {
#if WINDOWS_PHONE
				        foreach (var header in this.HandShake.Headers)
				        {
				            client.Headers[header.Key] = header.Value;
				        }
#else
				        client.Headers.Add(this.HandShake.Headers);
#endif
				    }

#if WINDOWS_PHONE
				    var signal = new ManualResetEvent(false);
                    client.DownloadStringCompleted += (sender, e) =>
                        {
                            value = e.Result;
                            signal.Set();
                        };
                    client.DownloadStringAsync(new Uri(string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port,
                                        uri.Query)));

				    signal.WaitOne();
#else
				    value =
				        client.DownloadString(string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port,
				                                            uri.Query));
                    // #5 tkiley: The uri.Query is available in socket.io's handshakeData object during authorization
				    // 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling
				    if (string.IsNullOrEmpty(value))
				        errorText = "Did not receive handshake from server";
#endif
				}
				catch (WebException webEx)
				{
				    Debug.WriteLine(string.Format("Handshake threw an exception...{0}", webEx.Message));
				    switch (webEx.Status)
				    {
				        case WebExceptionStatus.ConnectFailure:
				            errorText = string.Format("Unable to contact the server: {0}", webEx.Status);
				            break;
				        case WebExceptionStatus.NameResolutionFailure:
				            errorText = string.Format("Unable to resolve address: {0}", webEx.Status);
				            break;
				        case WebExceptionStatus.ProtocolError:
				            var resp = webEx.Response as HttpWebResponse; //((System.Net.HttpWebResponse)(webEx.Response))
				            if (resp != null)
				            {
				                switch (resp.StatusCode)
				                {
				                    case HttpStatusCode.Forbidden:
				                        errorText = "Socket.IO Handshake Authorization failed";
				                        break;
				                    default:
				                        errorText = string.Format("Handshake response status code: {0}", resp.StatusCode);
				                        break;
				                }
				            }
				            else
				                errorText = string.Format("Error getting handshake from Socket.IO host instance: {0}", webEx.Message);
				            break;
				        default:
				            errorText = string.Format("Handshake threw an exception...{0}", webEx.Message);
				            break;
				    }
				}
				catch (Exception ex)
				{
					errorText = string.Format("Error getting handshake from Socket.IO host instance: {0}", ex.Message);
					//this.OnErrorEvent(this, new ErrorEventArgs(errMsg));
				}
			}
            finally
            {
#if !WINDOWS_PHONE
				    client.Dispose();
#endif
            }
			if (string.IsNullOrEmpty(errorText))
				this.HandShake.UpdateFromSocketIOResponse(value);
			else
				this.HandShake.ErrorMessage = errorText;
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		// The bulk of the clean-up code 
		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				// free managed resources
				this.Close();
				this.MessageQueueEmptyEvent.Dispose();
				this.ConnectionOpenEvent.Dispose();
			}
			
		}
	}


}
