using System.Reflection;
using System.Runtime.Loader;

var command = args.ElementAtOrDefault(0) ?? "help";
var gamePath = args.ElementAtOrDefault(1) ?? @"D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
var filter = args.ElementAtOrDefault(2) ?? string.Empty;

var devPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "XIVLauncher",
    "addon",
    "Hooks",
    "dev");

AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
{
    var candidate = Path.Combine(devPath, assemblyName.Name + ".dll");
    return File.Exists(candidate)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
        : null;
};

if (!Directory.Exists(gamePath))
{
    Console.Error.WriteLine($"Game sqpack path not found: {gamePath}");
    return 2;
}

var luminaAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(devPath, "Lumina.dll"));
var excelAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(devPath, "Lumina.Excel.dll"));
var gameDataType = luminaAssembly.GetType("Lumina.GameData") ?? throw new InvalidOperationException("Lumina.GameData not found.");

return command.ToLowerInvariant() switch
{
    "ctors" => PrintConstructors(gameDataType),
    "members" => PrintMembers(luminaAssembly, excelAssembly, filter),
    "types" => PrintSheetTypes(excelAssembly, filter),
    "sheets" => PrintRuntimeSheets(gameDataType, gamePath, filter),
    "dump" => DumpSheet(gameDataType, luminaAssembly, excelAssembly, gamePath, filter, args.ElementAtOrDefault(3)),
    "rawdump" => RawDumpSheet(gameDataType, luminaAssembly, gamePath, filter, args.ElementAtOrDefault(3)),
    _ => PrintHelp()
};

static int PrintConstructors(Type gameDataType)
{
    foreach (var constructor in gameDataType.GetConstructors())
        Console.WriteLine(constructor);

    return 0;
}

static int PrintSheetTypes(Assembly excelAssembly, string filter)
{
    var sheetTypes = excelAssembly.GetTypes()
        .Where(type => type.Namespace is "Lumina.Excel.Sheets" or "Lumina.Excel.Sheets.Experimental")
        .Where(type => type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(type => type.FullName);

    foreach (var type in sheetTypes)
        Console.WriteLine(type.FullName);

    return 0;
}

static int PrintMembers(Assembly luminaAssembly, Assembly excelAssembly, string typeName)
{
    var type = FindType(luminaAssembly, excelAssembly, typeName);
    if (type is null)
    {
        Console.Error.WriteLine($"Type not found: {typeName}");
        return 2;
    }

    Console.WriteLine(type.FullName);
    Console.WriteLine("Constructors:");
    foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine($"  {constructor}");

    Console.WriteLine("Properties:");
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).OrderBy(property => property.Name))
        Console.WriteLine($"  {property.PropertyType.FullName} {property.Name}");

    Console.WriteLine("Fields:");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).OrderBy(field => field.Name))
        Console.WriteLine($"  {field.FieldType.FullName} {field.Name}");

    Console.WriteLine("Methods:");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).OrderBy(method => method.Name))
        Console.WriteLine($"  {method}");

    return 0;
}

static Type? FindType(Assembly luminaAssembly, Assembly excelAssembly, string typeName)
{
    foreach (var assembly in new[] { luminaAssembly, excelAssembly })
    {
        var exact = assembly.GetType(typeName);
        if (exact is not null)
            return exact;

        var partial = assembly.GetTypes()
            .FirstOrDefault(type => type.FullName?.Contains(typeName, StringComparison.OrdinalIgnoreCase) == true);
        if (partial is not null)
            return partial;
    }

    return null;
}

static int PrintRuntimeSheets(Type gameDataType, string gamePath, string filter)
{
    var gameData = CreateGameData(gameDataType, gamePath);
    var excel = gameDataType.GetProperty("Excel")?.GetValue(gameData)
        ?? throw new InvalidOperationException("GameData.Excel not found.");

    var sheetNames = excel.GetType().GetProperty("SheetNames")?.GetValue(excel) as IEnumerable<string>
        ?? throw new InvalidOperationException("Excel.SheetNames not found.");

    foreach (var name in sheetNames
        .Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(name => name))
    {
        Console.WriteLine(name);
    }

    return 0;
}

static int DumpSheet(Type gameDataType, Assembly luminaAssembly, Assembly excelAssembly, string gamePath, string sheetName, string? limitArg)
{
    if (string.IsNullOrWhiteSpace(sheetName))
    {
        Console.Error.WriteLine("Sheet name is required.");
        return 2;
    }

    var limit = int.TryParse(limitArg, out var parsed) ? parsed : 20;
    var gameData = CreateGameData(gameDataType, gamePath);
    var excel = gameDataType.GetProperty("Excel")?.GetValue(gameData)
        ?? throw new InvalidOperationException("GameData.Excel not found.");

    var rowType = excelAssembly.GetType($"Lumina.Excel.Sheets.{sheetName}")
        ?? excelAssembly.GetType($"Lumina.Excel.Sheets.Experimental.{sheetName}");
    if (rowType is null)
    {
        Console.Error.WriteLine($"Generated row type not found for sheet: {sheetName}");
        return 3;
    }

    var getSheet = excel.GetType().GetMethods()
        .FirstOrDefault(method =>
            method.Name == "GetBaseSheet" &&
            method.GetParameters().Length == 3 &&
            method.GetParameters()[0].ParameterType == typeof(Type));

    if (getSheet is null)
    {
        Console.Error.WriteLine("Excel.GetBaseSheet(Type, ...) not found.");
        foreach (var method in excel.GetType().GetMethods().Where(method => method.Name.Contains("Sheet")))
            Console.Error.WriteLine(method);
        return 3;
    }

    object? sheet;
    try
    {
        sheet = getSheet.Invoke(excel, new object?[] { rowType, null, null });
    }
    catch (TargetInvocationException ex) when (ex.InnerException?.GetType().Name == "MismatchedColumnHashException")
    {
        Console.WriteLine(ex.InnerException.Message);
        Console.WriteLine("Falling back to raw dump.");
        return RawDumpSheet(gameDataType, luminaAssembly, gamePath, sheetName, limitArg);
    }
    if (sheet is null)
    {
        Console.Error.WriteLine($"Sheet not found: {sheetName}");
        return 4;
    }

    Console.WriteLine(sheet.GetType().FullName);
    Console.WriteLine($"Count: {sheet.GetType().GetProperty("Count")?.GetValue(sheet) ?? "?"}");

    var count = 0;
    foreach (var row in (System.Collections.IEnumerable)sheet)
    {
        Console.WriteLine($"--- row {GetPropertyValue(row, "RowId") ?? GetPropertyValue(row, "RowIdRaw") ?? count}");
        foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(property => property.Name))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(row);
            }
            catch (Exception ex)
            {
                value = $"<{ex.GetType().Name}>";
            }

            Console.WriteLine($"{property.Name}: {FormatValue(value)}");
        }

        count++;
        if (count >= limit)
            break;
    }

    return 0;
}

static int RawDumpSheet(Type gameDataType, Assembly luminaAssembly, string gamePath, string sheetName, string? limitArg)
{
    if (string.IsNullOrWhiteSpace(sheetName))
    {
        Console.Error.WriteLine("Sheet name is required.");
        return 2;
    }

    var limit = int.TryParse(limitArg, out var parsed) ? parsed : 20;
    var gameData = CreateGameData(gameDataType, gamePath);
    var excel = gameDataType.GetProperty("Excel")?.GetValue(gameData)
        ?? throw new InvalidOperationException("GameData.Excel not found.");

    var getRawSheet = excel.GetType().GetMethods()
        .FirstOrDefault(method => method.Name == "GetRawSheet" && method.GetParameters().Length == 2);
    if (getRawSheet is null)
    {
        Console.Error.WriteLine("Excel.GetRawSheet(string, ...) not found.");
        return 3;
    }

    var sheet = getRawSheet.Invoke(excel, new object?[] { sheetName, null });
    if (sheet is null)
    {
        Console.Error.WriteLine($"Raw sheet not found: {sheetName}");
        return 4;
    }

    var rawRowType = luminaAssembly.GetType("Lumina.Excel.RawRow")
        ?? throw new InvalidOperationException("Lumina.Excel.RawRow not found.");
    var rowFactoryMethods = sheet.GetType()
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .Where(method => method.Name == "UnsafeCreateRowAt")
        .ToList();
    if (rowFactoryMethods.Count == 0)
    {
        Console.Error.WriteLine($"No UnsafeCreateRowAt method on {sheet.GetType().FullName}");
        foreach (var method in sheet.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(method => method.Name.Contains("Row", StringComparison.OrdinalIgnoreCase)))
            Console.Error.WriteLine(method);
        return 5;
    }

    var createRowAt = rowFactoryMethods.First(method => method.IsGenericMethodDefinition).MakeGenericMethod(rawRowType);
    var readColumn = rawRowType.GetMethod("ReadColumn", new[] { typeof(int) })
        ?? throw new InvalidOperationException("RawRow.ReadColumn(int) not found.");

    var count = (int)(sheet.GetType().GetProperty("Count")?.GetValue(sheet) ?? 0);
    var columns = ((System.Collections.IEnumerable?)sheet.GetType().GetProperty("Columns")?.GetValue(sheet))
        ?.Cast<object>()
        .ToList() ?? [];

    Console.WriteLine(sheet.GetType().FullName);
    Console.WriteLine($"Count: {count}");
    Console.WriteLine($"ColumnHash: {sheet.GetType().GetProperty("ColumnHash")?.GetValue(sheet)}");
    Console.WriteLine("Columns:");
    for (var i = 0; i < columns.Count; i++)
    {
        var column = columns[i];
        var offset = column.GetType().GetField("Offset")?.GetValue(column);
        var type = column.GetType().GetField("Type")?.GetValue(column);
        Console.WriteLine($"  {i}: offset={offset}, type={type}");
    }

    for (var rowIndex = 0; rowIndex < Math.Min(count, limit); rowIndex++)
    {
        var row = createRowAt.Invoke(sheet, new object?[] { rowIndex });
        if (row is null)
            continue;

        Console.WriteLine($"--- row {GetPropertyValue(row, "RowId") ?? rowIndex}");
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            object? value;
            try
            {
                value = readColumn.Invoke(row, new object?[] { columnIndex });
            }
            catch (Exception ex)
            {
                value = $"<{ex.GetType().Name}>";
            }

            Console.WriteLine($"{columnIndex}: {FormatValue(value)}");
        }
    }

    return 0;
}

static object CreateGameData(Type gameDataType, string gamePath)
{
    foreach (var constructor in gameDataType.GetConstructors())
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            return constructor.Invoke(new object?[] { gamePath });

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(DirectoryInfo))
            return constructor.Invoke(new object?[] { new DirectoryInfo(gamePath) });

        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string))
            return constructor.Invoke(new[] { gamePath, CreateDefault(parameters[1].ParameterType) });
    }

    throw new InvalidOperationException("No supported GameData constructor found.");
}

static object? CreateDefault(Type type)
{
    return type.IsValueType ? Activator.CreateInstance(type) : null;
}

static object? GetPropertyValue(object value, string propertyName)
{
    return value.GetType().GetProperty(propertyName)?.GetValue(value);
}

static string FormatValue(object? value)
{
    if (value is null)
        return "null";

    if (value is string text)
        return text;

    var type = value.GetType();
    if (type.IsPrimitive || value is decimal)
        return value.ToString() ?? string.Empty;

    if (type.FullName?.StartsWith("Lumina.Excel.RowRef", StringComparison.Ordinal) == true)
    {
        var rowId = type.GetProperty("RowId")?.GetValue(value);
        var sheetName = type.GetProperty("SheetName")?.GetValue(value);
        return $"RowRef({sheetName}:{rowId})";
    }

    return value.ToString() ?? type.FullName ?? string.Empty;
}

static int PrintHelp()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  SheetProbe ctors [sqpackPath]");
    Console.WriteLine("  SheetProbe members [sqpackPath] <typeName>");
    Console.WriteLine("  SheetProbe types [sqpackPath] [filter]");
    Console.WriteLine("  SheetProbe sheets [sqpackPath] [filter]");
    Console.WriteLine("  SheetProbe dump [sqpackPath] <sheetName> [limit]");
    Console.WriteLine("  SheetProbe rawdump [sqpackPath] <sheetName> [limit]");
    return 0;
}
