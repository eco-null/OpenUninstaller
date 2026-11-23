using System.Collections;

namespace OpenUninstaller.Core.Localization;

// Format saved results on demand, so changing the UI language does not require running the operation again.
internal sealed class LocalizedMessages : IReadOnlyList<string>
{
    private readonly List<(string Key, object?[] Arguments)> _messages = [];

    public int Count => _messages.Count;

    public string this[int index] => Format(_messages[index]);

    public void Add(string key, params object?[] arguments) => _messages.Add((key, arguments));

    public IEnumerator<string> GetEnumerator() => _messages.Select(Format).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static string Format((string Key, object?[] Arguments) message) => UiText.F(message.Key, message.Arguments);
}
