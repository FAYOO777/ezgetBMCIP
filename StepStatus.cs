namespace EzGetBmcIp;

internal enum StepState
{
    Waiting,
    Running,
    Done,
    Failed
}

internal sealed class StepStatus
{
    public StepStatus(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }
    public string Description { get; set; }
    public StepState State { get; set; } = StepState.Waiting;
}
