using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Channels;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.InputMethodServices;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Engine;
using Engine.Interfaces;
using Engine.Xna;
using Java.IO;
using Java.Security.Cert;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Point = Engine.Point;

namespace Client.Android
{
    /// <summary>
    /// This is the main type for your game
    /// </summary>
    public class BingoGameClient : Microsoft.Xna.Framework.Game
    {

        GraphicsDeviceManager graphics;
        private IClient client;
        private IRenderer renderer;



        public BingoGameClient()
        {
            graphics = new GraphicsDeviceManager(this);
            //            Resolution.Init(ref graphics);
            Content.RootDirectory = "Content";
            graphics.SupportedOrientations = DisplayOrientation.Portrait | DisplayOrientation.PortraitDown;

            //             Resolution.SetVirtualResolution(430 * 2, 557 * 2);
            //             Resolution.SetResolution(430 , 557 , false);
        }

        /// <summary>
        /// Allows the game to perform any initialization it needs to before starting to run.
        /// This is where it can query for any required services and load any non-graphic
        /// related content.  Calling base.Initialize will enumerate through any components
        /// and initialize them as well.
        /// </summary>
        protected override void Initialize()
        {
            // TODO: Add your initialization logic here




            base.Initialize();
        }

        /// <summary>
        /// LoadContent will be called once per game and is the place to load
        /// all of your content.
        /// </summary>
        protected override void LoadContent()
        {
            /*
                        logoTexture = Content.Load<Texture2D>("images/cannonBalls/ball_inner.png");
                        spriteBatch = new SpriteBatch(graphics.GraphicsDevice);
            */

            client = new XnaClient();

            renderer = new XnaRenderer(GraphicsDevice, Content, graphics, client);
            client.LoadImages(renderer);
            bool opened = false;
            client.Init(renderer, new XnaClientSettings()
            {
                GetKeyboardInput = (callback) =>
                {
                    if (opened) return;
                    opened = true;
                    Guide.BeginShowKeyboardInput(PlayerIndex.One, "", "", "", (asyncer) =>
                    {
                        var endShowKeyboardInput = Guide.EndShowKeyboardInput(asyncer);
                        opened = false;
                        callback(endShowKeyboardInput);
                    }, null);
                },
                OneLayoutAtATime = true
            });
            graphics.SupportedOrientations = DisplayOrientation.LandscapeLeft;

        }


        /// <summary>
        /// Allows the game to run logic such as updating the world,
        /// checking for collisions, gathering input, and playing audio.
        /// </summary>
        /// <param name="gameTime">Provides a snapshot of timing values.</param>
        protected override void Update(GameTime gameTime)
        {

            if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed)
            {
                Exit();
            }


            var layoutManager = client.ScreenManager.CurrentScreen;


            var dragGesture = client.DragDragGestureManager.GetGeture();
            if (dragGesture != null)
            {
                if (layoutManager.HasLayout(dragGesture.Direction))
                {
                    if (dragGesture.Distance > DragGestureManager.TriggerDistance)
                    {
                        layoutManager.ChangeLayout(dragGesture.Direction);
                        client.DragDragGestureManager.ClearDataPointsTillUp();
                    }
                }
            }

            TouchCollection touchCollection = TouchPanel.GetState();

            if (touchCollection.Count == 2)
            {
                client.DragDragGestureManager.AddDataPoint(TouchType.TouchMove, (int)touchCollection[0].Position.X, (int)touchCollection[0].Position.Y);
            }
            else
            {
                client.DragDragGestureManager.ClearDataPoints();

                for (int index = 0; index < touchCollection.Count; index++)
                {
                    var touch = touchCollection[index];

                    switch (touch.State)
                    {
                        case TouchLocationState.Moved:
                            client.TouchEvent(TouchType.TouchMove, (int)touch.Position.X, (int)touch.Position.Y);
                            break;
                        case TouchLocationState.Pressed:
                            client.TouchEvent(TouchType.TouchDown, (int)touch.Position.X, (int)touch.Position.Y);
                            break;
                        case TouchLocationState.Released:
                            client.DragDragGestureManager.TouchUp();
                            client.TouchEvent(TouchType.TouchUp, (int)touch.Position.X, (int)touch.Position.Y);
                            break;
                    }
                }
            }

            client.Tick(gameTime.TotalGameTime);

            base.Update(gameTime);
        }

        /// <summary>
        /// This is called when the game should draw itself.
        /// </summary>
        /// <param name="gameTime">Provides a snapshot of timing values.</param>
        protected override void Draw(GameTime gameTime)
        {
            //            Resolution.BeginDraw();


            client.Draw(gameTime.TotalGameTime);

            base.Draw(gameTime);
        }
    }

}
