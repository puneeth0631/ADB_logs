//using System;
//using System.Drawing;
//using System.Windows.Forms;

//namespace TestCamera // IMPORTANT: Ensure this matches your exact namespace!
//{
//    // 1. This defines your three themes
//    public enum AppTheme
//    {
//        Light,
//        Dark,
//        Warm
//    }

//    // 2. This manages the themes across your app
//    public static class ThemeManager
//    {
//        // Starts the app in Dark mode by default
//        public static AppTheme CurrentTheme { get; set; } = AppTheme.Dark;

//        // This method applies the background/text colors to your Forms
//        public static void ApplyTheme(Form form)
//        {
//            if (CurrentTheme == AppTheme.Dark)
//            {
//                form.BackColor = Color.FromArgb(28, 28, 30);
//                form.ForeColor = Color.White;
//            }
//            else if (CurrentTheme == AppTheme.Light)
//            {
//                form.BackColor = Color.White;
//                form.ForeColor = Color.Black;
//            }
//            else if (CurrentTheme == AppTheme.Warm)
//            {
//                form.BackColor = Color.FromArgb(250, 240, 230);
//                form.ForeColor = Color.Black;
//            }
//        }
//    }
//}