using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Catverter.PackValidator.Tests;

public sealed class PackRepositoryValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"catverter-pack-validator-{Guid.NewGuid():N}");

    [Fact]
    public void Published_repository_has_twenty_power_conversions_in_every_pack()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "catalog.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory.FullName, "catalog.json")));

        var officialCount = 0;
        var contributedCount = 0;
        foreach (var entry in catalog.RootElement.GetProperty("packs").EnumerateArray())
        {
            var classification = entry.GetProperty("classification").GetString();
            if (classification == "official")
            {
                officialCount++;
            }
            else
            {
                contributedCount++;
            }

            var path = entry.GetProperty("file").GetString()!;
            using var content = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory.FullName,
                    path.Replace('/', Path.DirectorySeparatorChar))));

            var name = entry.GetProperty("id").GetString()!;
            var conversions = content.RootElement.GetProperty("conversions")
                .EnumerateArray().ToArray();
            Assert.Equal(20, conversions.Count(c =>
                c.GetProperty("quantity").GetString() == "Power"));

            Assert.All(conversions.Where(c =>
                    c.GetProperty("quantity").GetString() == "Power"),
                conversion =>
                {
                    var watts = conversion.GetProperty("value").GetDouble();
                    Assert.True(
                        watts > 0 && watts <= (double)decimal.MaxValue,
                        name + " has a Power magnitude outside the supported range");
                });
        }

        Assert.Equal(11, officialCount);
        Assert.Equal(1, contributedCount);
    }

    [Theory]
    [InlineData(1e27, true)]
    [InlineData(1e29, false)]
    [InlineData(1e41, false)]
    public void Validate_Rejects_unsupported_power_magnitudes(
        double watts, bool isValid)
    {
        CreateValidRepository();
        var packPath = Path.Combine(_root, "packs", "author", "sample.json");
        var pack = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
        var conversion = pack["conversions"]![0]!;
        conversion["quantity"] = "Power";
        conversion["unit"] = "Watt";
        conversion["value"] = watts;
        File.WriteAllText(packPath, pack.ToJsonString(), new UTF8Encoding(false));

        var catalogPath = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        catalog["packs"]![0]!["sha256"] =
            PackRepositoryValidator.ComputeCanonicalSha256(packPath);
        File.WriteAllText(
            catalogPath, catalog.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().Validate(_root);
        Assert.Equal(isValid, result.IsValid);
        if (!isValid)
        {
            Assert.Contains(result.Errors, error =>
                error.Contains("supported decimal range", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Kelvin", true)]
    [InlineData("DegreeCelsius", false)]
    [InlineData("DegreeFahrenheit", false)]
    public void Validate_requires_absolute_temperature_references(
        string unit, bool valid)
    {
        CreateValidRepository();
        var packPath = Path.Combine(_root, "packs", "author", "sample.json");
        var pack = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
        var conversion = pack["conversions"]![0]!;
        conversion["quantity"] = "Temperature";
        conversion["unit"] = unit;
        conversion["value"] = 293.15;
        File.WriteAllText(
            packPath, pack.ToJsonString(), new UTF8Encoding(false));

        var catalogPath = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        catalog["packs"]![0]!["sha256"] =
            PackRepositoryValidator.ComputeCanonicalSha256(packPath);
        File.WriteAllText(
            catalogPath, catalog.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().Validate(_root);
        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            Assert.Contains(result.Errors, error =>
                error.Contains("must use Kelvin", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Validate_AcceptsNestedAuthorPack()
    {
        CreateValidRepository();

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.PackCount);
        Assert.Equal(0, result.OfficialPackCount);
        Assert.Equal(1, result.ContributedPackCount);
    }

    [Fact]
    public void Validate_AcceptsOfficialPackWithReservedOwnership()
    {
        CreateValidRepository(
            catalogPath: "packs/namireki/sample.json",
            packAuthor: "namireki",
            packageId: "com.namirekistudio.catverter.pack.sample",
            classification: "official",
            maintainer: "namireki",
            authorName: "Namireki Studio",
            repository: "https://github.com/namireki/catverter-packs");

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.OfficialPackCount);
        Assert.Equal(0, result.ContributedPackCount);
    }

    [Theory]
    [InlineData("1.7.0")]
    [InlineData("1.12.0")]
    [InlineData("2.4.0")]
    public void Validate_AcceptsAnUpdatedSourceCatalogVersion(string version)
    {
        CreateValidRepository();
        SetSourceCatalogVersion(version);

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.12.0-preview")]
    public void Validate_RejectsMalformedSourceCatalogVersions(string version)
    {
        CreateValidRepository();
        SetSourceCatalogVersion(version);

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "catalog.json.sourceCatalog.version has an invalid value",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsLocaleFromIndependentSourceRevision()
    {
        CreateValidRepository(
            localeSourceRevision: "1111111111111111111111111111111111111111");

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.LocaleCount);
    }

    [Fact]
    public void Validate_RejectsContributedPackUsingOfficialNamespace()
    {
        CreateValidRepository(
            catalogPath: "packs/namireki/sample.json",
            packAuthor: "namireki",
            packageId: "com.namirekistudio.catverter.pack.sample",
            maintainer: "namireki");

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("reserved official namespace", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsLocaleMissingOfficialConversion()
    {
        CreateValidRepository(
            catalogPath: "packs/namireki/sample.json",
            packAuthor: "namireki",
            packageId: "com.namirekistudio.catverter.pack.sample",
            classification: "official",
            maintainer: "namireki",
            authorName: "Namireki Studio");
        var localePath = Path.Combine(_root, "locales", "it.json");
        var locale = JsonNode.Parse(File.ReadAllText(localePath, Encoding.UTF8))!.AsObject();
        locale["packs"]!["sample"]!["conversions"]!.AsObject().Remove("sample-mass");
        File.WriteAllText(localePath, locale.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "conversions is missing 'sample-mass'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMaintainerThatDoesNotMatchFolder()
    {
        CreateValidRepository(maintainer: "different-author");

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("must match the pack folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsUnknownClassification()
    {
        CreateValidRepository(classification: "trusted");

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "classification must be 'official' or 'contributed'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOfficialPackWithoutSourceProvenance()
    {
        CreateValidRepository(
            catalogPath: "packs/namireki/sample.json",
            packAuthor: "namireki",
            packageId: "com.namirekistudio.catverter.pack.sample",
            classification: "official",
            maintainer: "namireki",
            authorName: "Namireki Studio");
        var catalogPath = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath, Encoding.UTF8))!.AsObject();
        catalog["packs"]![0]!.AsObject().Remove("source");
        File.WriteAllText(catalogPath, catalog.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "source is required for an official pack",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOfficialPackWithFlatSourcePath()
    {
        CreateValidRepository(
            catalogPath: "packs/namireki/sample.json",
            packAuthor: "namireki",
            packageId: "com.namirekistudio.catverter.pack.sample",
            classification: "official",
            maintainer: "namireki",
            authorName: "Namireki Studio");
        var catalogPath = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath, Encoding.UTF8))!.AsObject();
        catalog["packs"]![0]!["source"]!["file"] = "packs/sample.json";
        File.WriteAllText(catalogPath, catalog.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "source.file must be 'packs/namireki/sample.json'",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.json", "traversal")]
    [InlineData("/packs/author/sample.json", "rooted")]
    [InlineData("sample.json", "packs/<author>/<pack-id>.json")]
    [InlineData("packs/author/nested/sample.json", "packs/<author>/<pack-id>.json")]
    [InlineData("packs\\author\\sample.json", "forward slashes")]
    public void Validate_RejectsUnsafeOrMalformedCatalogPaths(
        string catalogPath,
        string expectedError)
    {
        CreateValidRepository(catalogPath);

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsPackBelowAnExtraDirectory()
    {
        CreateValidRepository();
        var originalPath = Path.Combine(_root, "packs", "author", "sample.json");
        var nestedDirectory = Path.Combine(_root, "packs", "author", "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.Move(originalPath, Path.Combine(nestedDirectory, "sample.json"));

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "packs/<author>/<pack-id>.json",
                StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateCatalog_RefreshesNestedPackHash()
    {
        CreateValidRepository(hashOverride: new string('0', 64));

        var result = new PackRepositoryValidator().UpdateCatalog(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        var catalog = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_root, "catalog.json"),
            Encoding.UTF8));
        var actualHash = catalog.RootElement
            .GetProperty("packs")[0]
            .GetProperty("sha256")
            .GetString();
        var expectedHash = PackRepositoryValidator.ComputeCanonicalSha256(
            Path.Combine(_root, "packs", "author", "sample.json"));
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public void UpdateCatalog_RefreshesLocaleHash()
    {
        CreateValidRepository();
        var catalogPath = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath, Encoding.UTF8))!.AsObject();
        catalog["locales"]![0]!["sha256"] = new string('0', 64);
        File.WriteAllText(catalogPath, catalog.ToJsonString(), new UTF8Encoding(false));

        var result = new PackRepositoryValidator().UpdateCatalog(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        var updated = JsonDocument.Parse(File.ReadAllText(catalogPath, Encoding.UTF8));
        var actualHash = updated.RootElement
            .GetProperty("locales")[0]
            .GetProperty("sha256")
            .GetString();
        var expectedHash = PackRepositoryValidator.ComputeCanonicalSha256(
            Path.Combine(_root, "locales", "it.json"));
        Assert.Equal(expectedHash, actualHash);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void SetSourceCatalogVersion(string version)
    {
        var path = Path.Combine(_root, "catalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        catalog["sourceCatalog"]!["version"] = version;
        File.WriteAllText(
            path,
            catalog.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            new UTF8Encoding(false));
    }

    private void CreateValidRepository(
        string catalogPath = "packs/author/sample.json",
        string? hashOverride = null,
        string packAuthor = "author",
        string packageId = "io.github.author.catverter.sample",
        string classification = "contributed",
        string maintainer = "author",
        string authorName = "Example Author",
        string repository = "https://github.com/author/catverter-packs",
        string localeSourceRevision = "0000000000000000000000000000000000000000")
    {
        var packDirectory = Path.Combine(_root, "packs", packAuthor);
        Directory.CreateDirectory(packDirectory);
        var packPath = Path.Combine(packDirectory, "sample.json");
        File.WriteAllText(
            packPath,
            $$"""
            {
              "$schema": "../../schemas/catverter-pack-v1.schema.json",
              "documentType": "catverter-pack",
              "schemaVersion": 1,
              "packageId": "{{packageId}}",
              "id": "sample",
              "name": "Sample",
              "version": "1.0.0",
              "minimumAppVersion": "1.0.0",
              "description": "Sample pack.",
              "metadata": {
                "authors": ["{{authorName}}"],
                "license": "Apache-2.0",
                "repository": "{{repository}}"
              },
              "conversions": [
                {
                  "id": "sample-mass",
                  "singularName": "sample",
                  "pluralName": "samples",
                  "quantity": "Mass",
                  "unit": "Gram",
                  "value": 1
                }
              ]
            }
            """,
            new UTF8Encoding(false));

        var hash = hashOverride ?? PackRepositoryValidator.ComputeCanonicalSha256(packPath);
        var localeDirectory = Path.Combine(_root, "locales");
        Directory.CreateDirectory(localeDirectory);
        var localePath = Path.Combine(localeDirectory, "it.json");
        File.WriteAllText(
            localePath,
            """
            {
              "$schema": "../schemas/catverter-locale-v1.schema.json",
              "documentType": "catverter-locale",
              "schemaVersion": 1,
              "locale": "it",
              "name": "Italiano",
              "strings": {},
              "packs": {
                "sample": {
                  "name": "Esempio",
                  "description": "Pacchetto di esempio.",
                  "conversions": {
                    "sample-mass": {
                      "one": "esempio",
                      "other": "esempi"
                    }
                  }
                }
              }
            }
            """,
            new UTF8Encoding(false));
        var localeHash = PackRepositoryValidator.ComputeCanonicalSha256(localePath);
        var catalog = $$"""
            {
              "$schema": "schemas/catverter-catalog-v1.schema.json",
              "documentType": "catverter-catalog",
              "schemaVersion": 1,
              "repositoryId": "io.github.author.catverter.packs",
              "version": "1.0.0",
              "minimumAppVersion": "1.0.0",
              "contentBaseUrl": "https://raw.githubusercontent.com/namireki/catverter-packs/{revision}/",
              "sourceCatalog": {
                "repository": "https://github.com/namireki/CatverterV2",
                "revision": "0000000000000000000000000000000000000000",
                "file": "packs/catalog.json",
                "repositoryId": "com.namirekistudio.catverter.packs",
                "version": "1.7.0"
              },
              "packs": [
                {
                  "packageId": "{{packageId}}",
                  "id": "sample",
                  "version": "1.0.0",
                  "classification": "{{classification}}",
                  "maintainer": "{{maintainer}}",
                  "file": "{{catalogPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "sha256": "{{hash}}",
                  "source": {
                    "repository": "https://github.com/namireki/CatverterV2",
                    "revision": "0000000000000000000000000000000000000000",
                    "file": "packs/{{packAuthor}}/sample.json",
                    "sha256": "{{hash}}"
                  }
                }
              ],
              "locales": [
                {
                  "locale": "it",
                  "name": "Italiano",
                  "file": "locales/it.json",
                  "sha256": "{{localeHash}}",
                  "source": {
                    "repository": "https://github.com/namireki/CatverterV2",
                    "revision": "{{localeSourceRevision}}",
                    "file": "packs/locales/it.json",
                    "sha256": "{{localeHash}}"
                  }
                }
              ]
            }
            """;
        File.WriteAllText(
            Path.Combine(_root, "catalog.json"),
            catalog,
            new UTF8Encoding(false));
    }
}
