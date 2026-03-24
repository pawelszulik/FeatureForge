using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatureForge.Models;
using Microsoft.Extensions.Configuration;

namespace FeatureForge.Services;

public class ProfileLoaderService
{
    public IReadOnlyList<ProjectProfile> Profiles { get; }

    public ProfileLoaderService(IConfiguration config)
    {
        var dir = config["Profiles:Directory"]
            ?? Path.Combine(AppContext.BaseDirectory, "Profiles");

        if (!Directory.Exists(dir))
            throw new InvalidOperationException(
                $"Katalog profili nie istnieje: {dir}\n" +
                $"Utwórz katalog i dodaj pliki *.json z definicjami profili.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var profiles = new List<ProjectProfile>();
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ProjectProfile>(json, options)
                    ?? throw new InvalidOperationException($"Deserializacja zwróciła null: {file}");
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Błąd wczytywania profilu '{file}': {ex.Message}", ex);
            }
        }

        if (profiles.Count == 0)
            throw new InvalidOperationException($"Brak plików *.json w katalogu profili: {dir}");

        Profiles = profiles;
    }
}
