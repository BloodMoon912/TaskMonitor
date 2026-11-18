using System;
using System.Windows.Markup;

namespace task_monitor.Localization
{
    /// <summary>
    /// XAML localisation: <c>Text="{loc:Tr settings.appearance.compact.title}"</c> with
    /// <c>xmlns:loc="clr-namespace:task_monitor.Localization"</c> on the root element.
    ///
    /// <para>Deliberately returns the translated STRING, not a binding: a binding would keep the
    /// element subscribed to the locale forever, and these texts only change when the whole
    /// window is rebuilt on a language switch anyway (<see cref="TaskMonitor"/> App.SetLanguage
    /// re-creates the windows), so a one-shot lookup is both cheaper and leak-free.</para>
    ///
    /// <para>Also safe on <c>Run.Text</c>, which is NOT a dependency property on net48: a markup
    /// extension gives the parser a plain value, so nothing tries to bind it.</para>
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension()
        {
        }

        public TrExtension(string id)
        {
            Id = id;
        }

        /// <summary>The string id from <c>lang/*.yaml</c> (area.component.field).</summary>
        [ConstructorArgument("id")]
        public string Id { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Id);
    }
}
