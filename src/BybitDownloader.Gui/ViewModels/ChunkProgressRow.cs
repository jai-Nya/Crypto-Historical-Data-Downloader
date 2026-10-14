namespace BybitDownloader.Gui.ViewModels;

public sealed class ChunkProgressRow : ObservableObject
{
    private string _status;
    private long _rows;
    private int _retries;

    public ChunkProgressRow(string chunkId, string status, long rows)
    {
        ChunkId = chunkId;
        _status = status;
        _rows = rows;
    }

    public string ChunkId { get; }

    /// <summary>Symbol parsed from the chunk id ("{exchange}/{category}/{symbol}/{yyyyMM}").</summary>
    public string Symbol => Segment(2);

    /// <summary>Category parsed from the chunk id.</summary>
    public string Category => Segment(1);

    /// <summary>Year-month parsed from the chunk id.</summary>
    public string Period => Segment(3);

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public long Rows
    {
        get => _rows;
        set
        {
            if (SetProperty(ref _rows, value)) OnPropertyChanged(nameof(RowsText));
        }
    }

    public int Retries
    {
        get => _retries;
        set
        {
            if (SetProperty(ref _retries, value)) OnPropertyChanged(nameof(RetriesText));
        }
    }

    public string RowsText => _rows.ToString("N0");
    public string RetriesText => _retries == 0 ? "" : _retries.ToString();

    private string Segment(int index)
    {
        var parts = ChunkId.Split('/');
        return index < parts.Length ? parts[index] : "";
    }
}
