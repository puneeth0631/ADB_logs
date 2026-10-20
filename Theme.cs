//using System;
//using System.Drawing;
//using System.Drawing.Drawing2D;
//using System.Windows.Forms;

//namespace TestCamera // IMPORTANT: Change "TestCamera" to your actual project namespace!
//{
//    public class RoundedThemeButton : Button
//    {
//        // This property allows the border color to be changed dynamically!
//        public Color BorderColor { get; set; } = Color.FromArgb(18, 25, 35);

//        public RoundedThemeButton()
//        {
//            // 1. Basic minimal setup
//            FlatStyle = FlatStyle.Flat;
//            FlatAppearance.BorderSize = 0; // Hides the ugly default square border

//            // 2. Default Colors (Starting in Dark Mode)
//            BackColor = Color.FromArgb(28, 28, 30); // Dark background
//            ForeColor = Color.White;                // White Icon
//            Size = new Size(50, 50);
//            Cursor = Cursors.Hand;

//            // 3. The built-in Windows 10/11 icon font for the sun
//            Font = new Font("Segoe MDL2 Assets", 16, FontStyle.Regular);
//            Text = "\uE706"; // This is the code for the modern Windows Sun icon
//        }

//        protected override void OnPaint(PaintEventArgs e)
//        {
//            base.OnPaint(e); // Draws the background and the sun icon

//            // 4. Turn on anti-aliasing for smooth rounded edges
//            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

//            // 5. Border settings
//            int radius = 14; // How round the corners are
//            float thickness = 4f; // How thick the border is

//            // 6. Draw the rounded border manually
//            using (GraphicsPath path = new GraphicsPath())
//            {
//                // Create the curved rectangle shape
//                RectangleF rect = new RectangleF(thickness / 2, thickness / 2, Width - thickness, Height - thickness);

//                path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
//                path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
//                path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
//                path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
//                path.CloseFigure();

//                // 7. Paint the outline using the dynamic BorderColor property!
//                using (Pen pen = new Pen(BorderColor, thickness))
//                {
//                    e.Graphics.DrawPath(pen, path);
//                }
//            }
//        }
//    }
//}