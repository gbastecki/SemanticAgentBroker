namespace SemanticAgentBroker.Client.Components;

public class SelectOptionItem<TValue>
{
    public TValue? Value { get; set; }
    public string Text { get; set; } = string.Empty;

    public SelectOptionItem(TValue? value, string text)
    {
        Value = value;
        Text = text;
    }
}
