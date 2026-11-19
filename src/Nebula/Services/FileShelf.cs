using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Nebula.Interop;
using Nebula.Models;

namespace Nebula.Services;

public sealed class ShelfItem : ObservableObject
{
    private string _name = "";

    public string Id { get; init; } = "";
    public string StoredPath { get; init; } = "";
    public string OriginalPath { get; init; } = "";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public long Size { get; init; }
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
    public ImageSource? Icon { get; init; }

    public string SizeText => Size switch
    {
        >= 1_073_741_824 => $"{Size / 1_073_741_824.0:0.#} GB",
        >= 1_048_576 => $"{Size / 1_048_576.0:0.#} MB",
        >= 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size} B"
    };

    public string AddedText => AddedUtc.ToLocalTime().ToString("HH:mm");
}

/// <summary>
/// A temporary drawer for files: dropping a file on the island stores a copy in
/// %APPDATA%\Nebula\shelf and keeps it there until it is dragged out, moved to a
/// folder or removed.
/// </summary>
public sealed class FileShelf : ObservableObject, IDisposable
{
    private readonly ActivityCenter _activities;
    private readonly object _gate = new();

    public FileShelf(ActivityCenter activities)
    {
        _activities = activities;
        Directory.CreateDirectory(ShelfDirectory);
        Load();
    }

    public static string ShelfDirectory => Path.Combine(SettingsStore.DataDirectory, "shelf");

    private static string IndexPath => Path.Combine(SettingsStore.DataDirectory, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public event Action? Changed;

    public async Task<(int Added, int Skipped, string? Error)> AddFilesAsync(IEnumerable<string> paths)
    {
        List<string> files = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int skipped = files.Count(path => !File.Exists(path));
        files = files.Where(File.Exists).ToList();
        if (files.Count == 0)
            return (0, skipped, "Nessun file valido: le cartelle non vengono copiate.");

        SystemActivity? activity = _activities.BeginOperation(
            ActivityKind.Copy,
            files.Count == 1 ? "Aggiunta al cassetto" : $"{files.Count} file al cassetto",
            Path.GetFileName(files[0]));

        int added = 0;
        string? error = null;

        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                string source = files[index];
                try
                {
                    string destination = UniqueDestination(Path.GetFileName(source));
                    await Task.Run(() => CopyWithoutProgress(source, destination));
                    var info = new FileInfo(destination);
                    ShelfItem item = CreateItem(destination, source, info);
                    Items.Insert(0, item);
                    added++;
                    _activities.UpdateOperation(activity,
                        (index + 1) * 100.0 / files.Count,
                        $"{index + 1}/{files.Count} · {item.Name}");
                }
                catch (Exception ex)
                {
                    error = "Un file non è stato copiato: " + ex.Message;
                    Log.Write("Shelf copy failed: " + ex);
                }
            }
        }
        finally
        {
            Save();
            Changed?.Invoke();
            if (added > 0)
                _activities.CompleteOperation(activity, added == 1 ? "File conservato" : $"{added} file conservati");
            else
                _activities.CompleteOperation(activity, "Nessun file copiato");
        }

        return (added, skipped, error);
    }

    public bool Remove(ShelfItem item)
    {
        try
        {
            if (File.Exists(item.StoredPath))
                File.Delete(item.StoredPath);
        }
        catch (Exception ex)
        {
            Log.Write("Shelf delete failed: " + ex.Message);
            return false;
        }

        Items.Remove(item);
        Save();
        Changed?.Invoke();
        return true;
    }

    public bool MoveTo(ShelfItem item, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder))
            return false;

        try
        {
            string target = Path.Combine(destinationFolder, item.Name);
            if (File.Exists(target))
            {
                string name = Path.GetFileNameWithoutExtension(item.Name);
                string extension = Path.GetExtension(item.Name);
                target = Path.Combine(destinationFolder, $"{name} ({DateTime.Now:HHmmss}){extension}");
            }

            File.Move(item.StoredPath, target);
            Items.Remove(item);
            Save();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Shelf move failed: " + ex.Message);
            return false;
        }
    }

    public bool Open(ShelfItem item) => AppLauncher.Launch(item.StoredPath, out _);

    public bool Reveal(ShelfItem item) => AppLauncher.RevealInExplorer(item.StoredPath, out _);

    public void Clear()
    {
        foreach (ShelfItem item in Items.ToList())
        {
            try
            {
                if (File.Exists(item.StoredPath))
                    File.Delete(item.StoredPath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        Items.Clear();
        Save();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Save();
    }

    private ShelfItem CreateItem(string storedPath, string originalPath, FileInfo info) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Path.GetFileName(storedPath),
        StoredPath = storedPath,
        OriginalPath = originalPath,
        Size = info.Length,
        AddedUtc = DateTime.UtcNow,
        Icon = IconExtractor.FromPath(storedPath)
    };

    private static void CopyWithoutProgress(string source, string destination)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);

            output.Flush(true);
        }
        catch
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
            catch
            {
                // Ignore cleanup failure.
            }

            throw;
        }
    }

    private string UniqueDestination(string fileName)
    {
        string safe = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        string candidate = Path.Combine(ShelfDirectory, safe);
        if (!File.Exists(candidate))
            return candidate;

        string name = Path.GetFileNameWithoutExtension(safe);
        string extension = Path.GetExtension(safe);
        int index = 2;
        do
        {
            candidate = Path.Combine(ShelfDirectory, $"{name} ({index++}){extension}");
        } while (File.Exists(candidate));

        return candidate;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return;

            var data = JsonSerializer.Deserialize<ShelfData>(File.ReadAllText(IndexPath));
            if (data?.Items is null)
                return;

            foreach (ShelfItemData item in data.Items)
            {
                if (string.IsNullOrWhiteSpace(item.StoredPath) || !File.Exists(item.StoredPath))
                    continue;

                var info = new FileInfo(item.StoredPath);
                Items.Add(new ShelfItem
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    Name = Path.GetFileName(item.StoredPath),
                    StoredPath = item.StoredPath,
                    OriginalPath = item.OriginalPath ?? "",
                    Size = info.Length,
                    AddedUtc = item.AddedUtc == default ? DateTime.UtcNow : item.AddedUtc,
                    Icon = IconExtractor.FromPath(item.StoredPath)
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shelf load failed: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            lock (_gate)
            {
                var data = new ShelfData
                {
                    Items = Items.Select(item => new ShelfItemData
                    {
                        Id = item.Id,
                        StoredPath = item.StoredPath,
                        OriginalPath = item.OriginalPath,
                        AddedUtc = item.AddedUtc
                    }).ToList()
                };

                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.WriteAllText(IndexPath,
                    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shelf save failed: " + ex.Message);
        }
    }

    private sealed class ShelfData
    {
        public List<ShelfItemData> Items { get; set; } = new();
    }

    private sealed class ShelfItemData
    {
        public string Id { get; set; } = "";
        public string StoredPath { get; set; } = "";
        public string? OriginalPath { get; set; }
        public DateTime AddedUtc { get; set; }
    }
}
