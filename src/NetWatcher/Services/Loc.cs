using System.Windows;

namespace NetWatcher.Services;

/// <summary>
/// UI text lookup. All visible strings live in Resources/Strings.*.xaml, so adding Portuguese
/// later means adding one translated file and calling <see cref="SetLanguage"/>.
/// </summary>
public static class Loc
{
    public static string Current { get; private set; } = "en";

    public static string T(string key, params object?[] args)
    {
        var text = Application.Current?.TryFindResource(key) as string ?? key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    public static void SetLanguage(string code)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Strings.") == true);
        var fresh = new ResourceDictionary { Source = new Uri($"Resources/Strings.{code}.xaml", UriKind.Relative) };
        if (old is not null) dicts[dicts.IndexOf(old)] = fresh;
        else dicts.Add(fresh);
        Current = code;
    }
}
