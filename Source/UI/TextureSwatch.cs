using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.UI;

/// <summary>
/// One draggable texture thumbnail in the Textures library. Drag it onto an object (in the
/// viewport) or onto the Texture slot in the inspector to apply it; the payload is just the
/// texture's res:// path (see <see cref="TextureDrag"/>).
/// </summary>
public partial class TextureSwatch : TextureRect
{
    public string TexturePath { get; private set; }

    private const int SwatchSize = 56;
    private const int PreviewSize = 48;

    public void Setup(TextureItem item)
    {
        TexturePath = item.Path;
        Texture = TextureLoader.Load(item.Path); // raw-decode fallback so a just-added texture shows before reimport
        // IgnoreSize: don't let the texture's native resolution drive the control size; ShrinkCenter:
        // don't let the flow container stretch it. Together they pin the thumbnail to SwatchSize.
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        CustomMinimumSize = new Vector2(SwatchSize, SwatchSize);
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        TooltipText = $"{item.Group}/{item.Name}";
        MouseFilter = MouseFilterEnum.Stop; // needed to originate a drag
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        // Without IgnoreSize the preview takes the texture's full pixel size — a "giant picture"
        // dragged around. Pin it to a small thumbnail.
        SetDragPreview(new TextureRect
        {
            Texture = Texture,
            ExpandMode = ExpandModeEnum.IgnoreSize,
            StretchMode = StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(PreviewSize, PreviewSize),
            Size = new Vector2(PreviewSize, PreviewSize),
        });
        return TextureDrag.Payload(TexturePath);
    }
}
