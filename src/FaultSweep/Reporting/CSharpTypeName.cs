namespace FaultSweep.Reporting;

internal static class CSharpTypeName
{
    public static string Format(Type type)
    {
        if (type.IsArray)
        {
            return Format(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (!type.IsGenericType)
        {
            return "global::" + type.FullName!.Replace('+', '.');
        }

        string[] parts = type.GetGenericTypeDefinition().FullName!.Split('+');
        Type[] arguments = type.GetGenericArguments();
        int consumed = 0;
        List<string> names = [];
        foreach (string part in parts)
        {
            int marker = part.IndexOf('`');
            if (marker < 0)
            {
                names.Add(part);
                continue;
            }
            int count = int.Parse(part[(marker + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            names.Add(part[..marker] + "<" + string.Join(", ", arguments.Skip(consumed).Take(count).Select(Format)) + ">");
            consumed += count;
        }
        return "global::" + string.Join(".", names);
    }
}
