﻿
using BingoBlockParty.Client.BallGame.Pieces;
using BingoBlockParty.Client.Utils;
using BingoBlockParty.Common.BallGame;
using BingoBlockParty.Common.BallGame.Models;
using BingoBlockParty.Common.BallGame.Pieces;
using BingoBlockParty.Common.BallGame.Planes;
using Engine.Interfaces;

namespace BingoBlockParty.Client.BallGame.Planes
{
    public class ClientCannonBallPlane : CannonBallPlane
    {

        public ILayer Plane { get; set; }

        public ClientCannonBallPlane(GameBoard gameBoard)
            : base(gameBoard)
        {
        }

        public override void Init()
        {
            base.Init();
            Plane = GameBoard.Client().Renderer.CreateLayer(GameBoard.GameModel.Client().CanvasWidth, GameBoard.GameModel.Client().CanvasHeight);
        }


        public override CannonBall CreateCannonBall(int x, int y, int angle)
        {
            return new ClientCannonBall(GameBoard, x, y, angle);
        }

        public override void RoundOver(RoundOverState state)
        {
            base.RoundOver(state);
            if (state==RoundOverState.Post)
            {
                this.Plane.Clear();
            }
        }

        public void Render()
        {
            if (this.CannonBall!=null)
            {
                this.Plane.Clear();
                var context = this.Plane;
                context.Save();

                this.GameBoard.Client().ViewManager.TranslateContext(context);

                context.Save();
                this.CannonBall.Client().Render(context);
                context.Restore();

                context.Restore();
            }

        }
    }

}