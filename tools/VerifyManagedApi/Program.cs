using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 3)
{
    throw new ArgumentException("Provide the Windows, Linux and macOS managed assemblies.");
}

var apiSurfaces = new List<string[]>();
foreach (var path in args)
{
    var context = new AssemblyLoadContext(Path.GetFullPath(path), isCollectible: true);
    try
    {
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        var api = assembly.GetExportedTypes().SelectMany(type =>
            new[] { $"{type.FullName}: {type.BaseType?.FullName}; {string.Join(',', type.GetInterfaces().Select(i => i.FullName).Order())}" }
                .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(member => $"{type.FullName}.{member.MemberType}: {member}"))).Order().ToArray();
        apiSurfaces.Add(api);
    }
    finally
    {
        context.Unload();
    }
}

for (var index = 1; index < apiSurfaces.Count; index++)
{
    if (!apiSurfaces[0].SequenceEqual(apiSurfaces[index]))
    {
        throw new InvalidOperationException($"Public API differs between {args[0]} and {args[index]}.");
    }
}

Console.WriteLine("Windows, Linux and macOS managed implementations expose the same public API.");
