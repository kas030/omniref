namespace OmniRef.Core.Models;

public enum ItemKind
{
    Image,
    File,
    Folder,
    Text,
    Url,
    Frame
}

public enum AssetMode
{
    ExternalReference,
    EmbeddedCopy
}

public enum TextHorizontalAlignment
{
    Left,
    Center,
    Right
}

public enum WorkspaceOpenMode
{
    ReadWrite,
    ReadOnly
}
