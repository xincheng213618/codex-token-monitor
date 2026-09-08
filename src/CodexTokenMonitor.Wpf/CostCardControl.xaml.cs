using System.Windows.Controls;

namespace CodexTokenMonitor;

/// <summary>Presentation of one cost figure; pricing stays with the caller.</summary>
internal partial class CostCardControl : UserControl
{
    public CostCardControl(string provider, string model, string amount, string? explanation, bool actual)
    {
        InitializeComponent();
        ProviderText.Text = provider;
        ModelText.Text = model;
        AmountText.Text = amount;
        ToolTip = explanation ?? $"{provider} · {model}\n{amount}";
        if (actual)
        {
            CardBorder.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            AmountText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
    }
}
