using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Mihomo.Desktop.Services;

/// <summary>Localizes interface text explicitly; configuration, node names and logs remain untouched.</summary>
public static partial class L
{
    private static readonly CultureInfo systemCulture = CultureInfo.CurrentUICulture;
    private static readonly Dictionary<string, string> english = BuildCatalog();
    private static readonly LanguageRevision revision = new();
    public static string Language { get; private set; } = "zh-CN";
    public static string ResolvedLanguage => Language == "system" ? (systemCulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US") : Language;
    public static bool IsEnglish => ResolvedLanguage == "en-US";
    public static event Action? Changed;

    private static Dictionary<string, string> BuildCatalog()
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterCommon(catalog);
        RegisterShell(catalog);
        RegisterProxyEditor(catalog);
        RegisterSearchSettings(catalog);
        RegisterDns(catalog);
        return catalog;
    }
    static partial void RegisterCommon(Dictionary<string, string> catalog);
    static partial void RegisterShell(Dictionary<string, string> catalog);
    static partial void RegisterProxyEditor(Dictionary<string, string> catalog);
    static partial void RegisterSearchSettings(Dictionary<string, string> catalog);
    static partial void RegisterDns(Dictionary<string, string> catalog);

    public static string T(string text) => IsEnglish && english.TryGetValue(text, out var translated) ? translated : text;
    public static string F(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, T(template), arguments);

    public static TElement Bind<TElement>(TElement element, DependencyProperty property, string text) where TElement : DependencyObject
    {
        BindingOperations.SetBinding(element, property, CreateBinding(text));
        return element;
    }
    internal static Binding CreateBinding(string text) => new(nameof(LanguageRevision.Value))
    {
        Source = revision, Mode = BindingMode.OneWay, Converter = TranslationConverter.Instance, ConverterParameter = text
    };
    internal static void SetLanguage(string language)
    {
        if (language is not ("zh-CN" or "en-US" or "system")) throw new ArgumentException("Unknown language", nameof(language));
        Language = language;
        var culture = CultureInfo.GetCultureInfo(ResolvedLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        revision.Refresh();
        Changed?.Invoke();
    }
    private sealed class LanguageRevision : INotifyPropertyChanged
    {
        public int Value { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() { Value++; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
    }
    private sealed class TranslationConverter : IValueConverter
    {
        internal static readonly TranslationConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public string Text { get; set; } = "";
    public TrExtension() { }
    public TrExtension(string text) => Text = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.CreateBinding(Text).ProvideValue(serviceProvider);
}
