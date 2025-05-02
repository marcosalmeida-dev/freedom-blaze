namespace FreedomBlaze.Components;

public class ThemeManager
{
    public event Func<bool, Task>? OnThemeChanged;
    private bool _isDarkMode;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            _isDarkMode = value;
            OnThemeChanged?.Invoke(_isDarkMode);
        }
    }

    public void ToggleTheme() => IsDarkMode = !IsDarkMode;
}
