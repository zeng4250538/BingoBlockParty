using System;
using Engine.Html5.Network;
using Engine.Interfaces;

namespace Engine.Html5.Web
{
    public class WebClient : IClient
    {
        public IScreenManager ScreenManager { get; set; }
        public ISocketManager SocketManager { get; set; }
        public IClientSettings ClientSettings { get; set; }
        public DragGestureManager DragDragGestureManager { get; set; }


        public IGame Game { get; set; }
        public WebRenderer Renderer { get; set; }

        public WebClient()
        {
            Game = new BingoBlockParty.Client.Game(this);
        }
        public void Init(IRenderer renderer, IClientSettings clientSettings)
        {

            Renderer = (WebRenderer)renderer;
            ClientSettings = clientSettings;
            ScreenManager = new WebScreenManager(Renderer, this);
            Game.InitScreens(renderer, ScreenManager);

            SocketManager = new WebSocketManager();
            Game.InitSocketManager(SocketManager);
        }

         

        public void Draw(TimeSpan elapsedGameTime)
        {
            Game.BeforeDraw();
            ScreenManager.Draw(elapsedGameTime);
            Game.AfterDraw();
        }

        public void TouchEvent(TouchType touchType, int x, int y)
        {
            ScreenManager.TouchEvent(touchType, x, y);
        }

        public void Tick(TimeSpan elapsedGameTime)
        {
            Game.BeforeTick();

            ScreenManager.Tick(elapsedGameTime);
            Game.AfterTick();
        }

        public void Timeout(Action callback, int ms)
        {
            Window.SetTimeout(callback, ms);
        }

        public void ShowKeyboard()
        {
            throw new NotImplementedException();
        }

        public void LoadImages(IRenderer renderer)
        {
            Game.LoadAssets(renderer);
        }
    }
    public class WebClientSettings : IClientSettings
    {
        public bool OneLayoutAtATime { get; set; }
        public Action<Action<string>> GetKeyboardInput { get; set; }
    }


}