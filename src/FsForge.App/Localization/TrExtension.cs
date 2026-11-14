using System.Windows.Markup;
using FsForge.Presentation.Services;

namespace FsForge.App.Localization;

/// <summary>
/// Interface text in XAML: <c>Header="{l:Tr 'Открыть…'}"</c>. The Russian text is the key (see <see cref="L"/>);
/// it is translated once, when the element is created, because the language is fixed at startup.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
