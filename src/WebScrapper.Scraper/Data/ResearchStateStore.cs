using System.Text.Json;
using WebScrapper.Scraper.Models;

namespace WebScrapper.Scraper.Data;

public static class ResearchStateStore
{
    public static string DedupeKey(ResearchItem item) => $"{item.Symbol}|{item.RecoPrice}";

    public static Dictionary<string, ResearchItem> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, ResearchItem>();
        }

        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<List<ResearchItem>>(json) ?? new List<ResearchItem>();
        return items.ToDictionary(DedupeKey);
    }

    public static void Save(string path, IEnumerable<ResearchItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}
