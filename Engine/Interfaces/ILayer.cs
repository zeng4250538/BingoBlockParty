﻿namespace Engine.Interfaces
{
    public interface ILayer
    {
        void Save();
        void Restore();
        void Translate(int x, int y);
        void DrawImage(IImage image, int x, int y);
        void DrawImage(IImage image, int x, int y, int width, int height);
        void DrawImage(IImage image, int x, int y, double angle, int centerX, int centerY);
        void DrawString(string text, int x, int y);
        void Clear();
        double MeasureString(string text);
        void  DrawRectangle(Color color, int x, int y, int width, int height);
    }
}