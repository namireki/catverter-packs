using System.Text;
using System.Text.Json;
using Xunit;

namespace Catverter.PackValidator.Tests;

public sealed class PackRepositoryValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"catverter-pack-validator-{Guid.NewGuid():N}");

    [Fact]
    public void Validate_AcceptsNestedAuthorPack()
    {
        CreateValidRepository();

        var result = new PackRepositoryValidator().Validate(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.PackCount);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void CreateValidRepository(
        string catalogPath = "packs/author/sample.json",
        string? hashOverride = null)
    {
        var packDirectory = Path.Combine(_root, "packs", "author");
        Directory.CreateDirectory(packDirectory);
        var packPath = Path.Combine(packDirectory, "sample.json");
        File.WriteAllText(
            packPath,
            """
            {
              "$schema": "../../schemas/catverter-pack-v1.schema.json",
              "documentType": "catverter-pack",
              "schemaVersion": 1,
              "packageId": "io.github.author.catverter.sample",
              "id": "sample",
              "name": "Sample",
              "version": "1.0.0",
              "minimumAppVersion": "1.0.0",
              "description": "Sample pack.",
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
        var catalog = $$"""
            {
              "$schema": "schemas/catverter-catalog-v1.schema.json",
              "documentType": "catverter-catalog",
              "schemaVersion": 1,
              "repositoryId": "io.github.author.catverter.packs",
              "version": "1.0.0",
              "minimumAppVersion": "1.0.0",
              "packs": [
                {
                  "packageId": "io.github.author.catverter.sample",
                  "id": "sample",
                  "version": "1.0.0",
                  "file": "{{catalogPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "sha256": "{{hash}}"
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
