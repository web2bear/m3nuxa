
namespace m3nuxa.App;

internal static class ActionParameters
{
    internal static string ExpandSingle(string? template, InvocationContext context, bool encodeUrlComponents = false)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException(L.Get("ActionPathRequired"));
        return TemplateExpander.Expand(template, context, encodeUrlComponents);
    }

    internal static IReadOnlyList<string> ExpandArguments(string? template, InvocationContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) return [];
        var arguments = new List<string>();
        foreach (var token in CommandLineTokenizer.Split(template))
        {
            if (token == "{selectedPaths}")
            {
                if (context.SelectedPaths.Count == 0) throw new InvalidOperationException(L.Get("ActionPathsMissing"));
                arguments.AddRange(context.SelectedPaths);
            }
            else arguments.Add(TemplateExpander.Expand(token, context));
        }
        return arguments;
    }
}
