using System.Windows;

namespace SennheiserTray
{
    public static class ThemeManager
    {
        private const string LightColorsUri = "pack://application:,,,/SennheiserTray;component/Themes/Colors.xaml";
        private const string DarkColorsUri = "pack://application:,,,/SennheiserTray;component/Themes/Colors.Dark.xaml";

        public static void Apply(bool isDarkTheme)
        {
            ResourceDictionary[] dictionaries =
                System.Windows.Application.Current.Resources.MergedDictionaries.ToArray();
            int colorsIndex = Array.FindIndex(
                dictionaries,
                dictionary => dictionary.Contains("AppBackgroundBrush"));
            if (colorsIndex < 0)
            {
                throw new InvalidOperationException("The application color resources could not be found.");
            }

            System.Windows.Application.Current.Resources.MergedDictionaries[colorsIndex] = new ResourceDictionary
            {
                Source = new Uri(isDarkTheme ? DarkColorsUri : LightColorsUri, UriKind.Absolute)
            };
        }
    }
}
