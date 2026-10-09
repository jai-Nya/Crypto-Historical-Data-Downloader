namespace BybitDownloader.Gui.ViewModels;

public sealed class ChunkProgressRow : ObservableObject
{
    private string _status;
    private long _rows;

    public ChunkProgressRow(string chunkId, string status, long rows)
    {
        ChunkId = chunkId;
        _status = status;
        _rows = rows;
    }

    public string ChunkId { get; }

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

    public string RowsText => _rows.ToString("N0");
}
