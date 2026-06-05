using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PyConnectKiosk;

public class RackViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public RackItem Rack { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public RackViewModel(RackItem rack) => Rack = rack;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// One display card per rack — shows all codes generated for that rack.
/// </summary>
public class OtcCardViewModel
{
    public RackItem        Rack           { get; }
    public List<CodeEntry> CodeEntries    { get; }
    public string          StatusLabel    { get; }
    public Brush           StatusColor    { get; }
    public Brush           CardBackground { get; }
    public Brush           BorderColor    { get; }

    public OtcCardViewModel(OtcResult r)
    {
        Rack        = r.Rack;
        CodeEntries = new List<CodeEntry>();

        if (r.Success)
        {
            for (int i = 0; i < r.Codes.Count; i++)
                CodeEntries.Add(new CodeEntry(
                    Label : r.Codes.Count > 1 ? $"Code {i + 1}" : "Code",
                    Code  : r.Codes[i],
                    Color : MakeBrush("#15803D")
                ));

            StatusLabel    = r.Codes.Count > 1 ? $"SUCCESS  ×{r.Codes.Count}" : "SUCCESS";
            StatusColor    = MakeBrush("#16A34A");
            CardBackground = MakeBrush("#F0FFF4");
            BorderColor    = MakeBrush("#86EFAC");
        }
        else
        {
            CodeEntries.Add(new CodeEntry("ERROR", r.ErrorCode, MakeBrush("#DC2626")));
            StatusLabel    = r.ErrorCode;
            StatusColor    = MakeBrush("#EF4444");
            CardBackground = MakeBrush("#FFF1F2");
            BorderColor    = MakeBrush("#FCA5A5");
        }
    }

    private static SolidColorBrush MakeBrush(string hex)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}

public record CodeEntry(string Label, string Code, Brush Color);
