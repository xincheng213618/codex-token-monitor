using System.Globalization;
using System.Windows;

namespace CodexTokenMonitor;

internal partial class PricePresetEditorWindow : Window
{
    private readonly PricePreset original;

    public PricePresetEditorWindow(PricePreset preset, bool isNew)
    {
        InitializeComponent();
        original = preset.Clone();
        Result = preset.Clone();
        Title = isNew ? "新增价格" : "编辑价格";
        HeadingText.Text = Title;
        CurrencyBox.ItemsSource = new[] { "$", "¥", "Credits" };
        UnitBox.ItemsSource = new[] { "USD / 1M tokens", "CNY / 1M tokens", "Credits / token" };
        LoadPreset(preset);
    }

    public PricePreset Result { get; private set; }

    private void LoadPreset(PricePreset preset)
    {
        ProviderBox.Text = preset.Provider;
        ModelBox.Text = preset.Model;
        CurrencyBox.Text = string.IsNullOrWhiteSpace(preset.CurrencySymbol) ? "$" : preset.CurrencySymbol;
        UnitBox.Text = string.IsNullOrWhiteSpace(preset.UnitLabel) ? "USD / 1M tokens" : preset.UnitLabel;
        InputBox.Text = FormatDecimal(preset.UncachedInput);
        CachedBox.Text = FormatDecimal(preset.CachedInput);
        OutputBox.Text = FormatDecimal(preset.Output);
        SourceBox.Text = preset.Source;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModelBox.Text))
        {
            ShowValidation("请填写模型名称。", ModelBox);
            return;
        }

        if (!TryReadPrice(InputBox.Text, out var input))
        {
            ShowValidation("未缓存输入价格必须是大于等于 0 的数字。", InputBox);
            return;
        }

        if (!TryReadPrice(CachedBox.Text, out var cached))
        {
            ShowValidation("缓存输入价格必须是大于等于 0 的数字。", CachedBox);
            return;
        }

        if (!TryReadPrice(OutputBox.Text, out var output))
        {
            ShowValidation("输出价格必须是大于等于 0 的数字。", OutputBox);
            return;
        }

        var unit = string.IsNullOrWhiteSpace(UnitBox.Text) ? "USD / 1M tokens" : UnitBox.Text.Trim();
        Result = new PricePreset
        {
            Group = original.Group,
            Provider = ProviderBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            CurrencySymbol = string.IsNullOrWhiteSpace(CurrencyBox.Text) ? "$" : CurrencyBox.Text.Trim(),
            UnitLabel = unit,
            Divisor = InferDivisor(unit, original.Divisor),
            UncachedInput = input,
            CachedInput = cached,
            Output = output,
            Source = SourceBox.Text.Trim()
        };
        DialogResult = true;
    }

    private void ShowValidation(string message, System.Windows.Controls.Control target)
    {
        System.Windows.MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        target.Focus();
    }

    private static bool TryReadPrice(string text, out decimal value)
    {
        var valid = decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
                    decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        return valid && value >= 0;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static decimal InferDivisor(string unit, decimal fallback)
    {
        if (unit.Contains("1M", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("million", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("百万", StringComparison.OrdinalIgnoreCase))
        {
            return 1_000_000m;
        }

        if (unit.Contains("/ token", StringComparison.OrdinalIgnoreCase) ||
            unit.Contains("每 token", StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        return fallback > 0 ? fallback : 1_000_000m;
    }
}
