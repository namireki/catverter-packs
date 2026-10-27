using Catverter.PackValidator;

if (args.Length != 2)
{
    PrintUsage();
    return 2;
}

var validator = new PackRepositoryValidator();
ValidationResult result;

try
{
    result = args[0] switch
    {
        "validate-pack-repository" => validator.Validate(args[1]),
        "update-catalog" => validator.UpdateCatalog(args[1]),
        _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    PrintUsage();
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Validation failed unexpectedly: {exception.Message}");
    return 1;
}

foreach (var error in result.Errors)
{
    Console.Error.WriteLine($"error: {error}");
}

if (!result.IsValid)
{
    Console.Error.WriteLine(
        $"Pack repository validation failed with {result.Errors.Count} error(s).");
    return 1;
}

var action = args[0] == "update-catalog" ? "Updated and validated" : "Validated";
Console.WriteLine(
    $"{action} {result.PackCount} pack(s): " +
    $"{result.OfficialPackCount} official, {result.ContributedPackCount} contributed; " +
    $"{result.LocaleCount} locale(s).");
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: Catverter.PackValidator " +
        "<validate-pack-repository|update-catalog> <repository-root>");
}
