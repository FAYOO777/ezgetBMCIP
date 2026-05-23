using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EzGetBmcIp;

public enum StepState
{
    Pending,
    Active,
    Done,
    Failed
}

public sealed class StepItem : INotifyPropertyChanged
{
    private string _description;
    private StepState _state;
    private bool _isFirst;
    private bool _isLast;
    private StepState _previousState;

    public string Title { get; }
    public string ShortTitle { get; }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public StepState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); }
    }

    public bool IsFirst
    {
        get => _isFirst;
        set { _isFirst = value; OnPropertyChanged(); }
    }

    public bool IsLast
    {
        get => _isLast;
        set { _isLast = value; OnPropertyChanged(); }
    }

    public StepState PreviousState
    {
        get => _previousState;
        set { _previousState = value; OnPropertyChanged(); }
    }

    public StepItem(string title, string shortTitle, string description)
    {
        Title = title;
        ShortTitle = shortTitle;
        _description = description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
