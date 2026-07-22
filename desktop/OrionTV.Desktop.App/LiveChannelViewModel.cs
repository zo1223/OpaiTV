using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>
/// 频道列表项视图模型：包裹不可变的 <see cref="LiveChannel"/> 记录，
/// 额外承载可变的测速状态（徽标颜色、延迟/速率标签），供 ListBox 的 ItemTemplate 绑定。
/// </summary>
public sealed class LiveChannelViewModel : INotifyPropertyChanged
{
    private static readonly Brush UnknownBrush = Frozen("#5A5F66");
    private static readonly Brush GoodBrush = Frozen("#00A862");
    private static readonly Brush FairBrush = Frozen("#E6A700");
    private static readonly Brush BadBrush = Frozen("#E5484D");

    private ChannelHealthResult _health = new(ChannelHealth.Unknown, 0, string.Empty);
    private bool _isProbing;

    public LiveChannelViewModel(string key, LiveChannel channel)
    {
        Key = key;
        Channel = channel;
    }

    /// <summary>批量探测时用于回填结果的稳定键（与频道在列表中的位置一一对应）。</summary>
    public string Key { get; }

    public LiveChannel Channel { get; }

    public string Name => Channel.Name;

    public string Url => Channel.Url;

    public ChannelHealthResult Health
    {
        get => _health;
        set
        {
            _health = value;
            IsProbing = false;
            OnPropertyChanged(nameof(Health));
            OnPropertyChanged(nameof(BadgeBrush));
            OnPropertyChanged(nameof(MetricLabel));
            OnPropertyChanged(nameof(DetailTooltip));
            OnPropertyChanged(nameof(MetricTooltip));
        }
    }

    /// <summary>该频道正在探测中（用于显示等待态）。</summary>
    public bool IsProbing
    {
        get => _isProbing;
        set
        {
            if (_isProbing == value)
            {
                return;
            }

            _isProbing = value;
            OnPropertyChanged(nameof(IsProbing));
            OnPropertyChanged(nameof(BadgeBrush));
            OnPropertyChanged(nameof(MetricLabel));
            OnPropertyChanged(nameof(MetricTooltip));
        }
    }

    public Brush BadgeBrush => _isProbing
        ? UnknownBrush
        : _health.Health switch
        {
            ChannelHealth.Good => GoodBrush,
            ChannelHealth.Fair => FairBrush,
            ChannelHealth.Bad => BadBrush,
            _ => UnknownBrush,
        };

    public string? DetailTooltip => string.IsNullOrEmpty(_health.Detail) ? Name : $"{Name} · {_health.Detail}";

    public string MetricLabel => _isProbing ? "测速中" : ChannelHealthDisplay.MetricsLabelOf(_health);

    public string MetricTooltip => $"{Name} · {ChannelHealthDisplay.MetricsLabelOf(_health)}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
