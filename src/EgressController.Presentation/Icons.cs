using Avalonia.Media;

namespace EgressController.Presentation;

/// <summary>Original 20×20 monochrome vector icons. No font or SVG runtime dependency.</summary>
public static class Icons
{
    public static Geometry Folder { get; } = For("folder");
    public static Geometry Guardian { get; } = For("guardian");
    public static Geometry For(string name) => StreamGeometry.Parse(name switch
    {
        "overview" => "M2 2H9V9H2Z M12 2H19V9H12Z M2 12H9V19H2Z M12 12H19V19H12Z",
        "network" => "M8 1H13V6H8Z M1 14H6V19H1Z M15 14H20V19H15Z M9 6H12V10H19V14H16V12H5V14H2V10H9Z",
        "apps" => "M2 2H19V19H2Z M4 4V6H17V4Z M4 9V17H17V9Z",
        "rules" => "M2 3H5V6H2Z M8 3H19V6H8Z M2 9H5V12H2Z M8 9H19V12H8Z M2 15H5V18H2Z M8 15H19V18H8Z",
        "connections" => "M1 4H5V8H1Z M16 4H20V8H16Z M5 5H16V7H5Z M1 14H5V18H1Z M16 14H20V18H16Z M5 15H16V17H5Z M9 5H11V16H9Z",
        "traffic" => "M2 12H6V19H2Z M9 7H13V19H9Z M16 2H20V19H16Z",
        "protection" => "M10 1L19 4V11C19 15 15 18 10 21C5 18 1 15 1 11V4Z M9 6V13H11V6Z M9 15V17H11V15Z",
        "guardian" => "M10 1L19 4V11C19 15 15 18 10 21C5 18 1 15 1 11V4Z M5 10L9 14L16 7L14.5 5.5L9 11L6.5 8.5Z",
        "events" => "M4 1H17L20 4V20H4Z M6 3V18H18V5H15V3Z M8 7H15V9H8Z M8 11H15V13H8Z M8 15H12V17H8Z",
        "folder" => "M1 4H9L11 7H20V18H1Z M3 9V16H18V9Z",
        "open" => "M2 4H10V6H4V17H16V11H18V19H2Z M11 1H20V10H18V4.5L9.5 13L8 11.5L16.5 3H11Z",
        "power" => "M9 1H12V11H9Z M5 4L7 6C1 10 4 18 10.5 18C17 18 20 10 14 6L16 4C24 10 20 21 10.5 21C1 21-3 10 5 4Z",
        "refresh" => "M17 4L20 1V9H12L15 6C9 1 2 6 4 13L1 14C-2 3 10-3 17 4Z M4 17L1 20V12H9L6 15C12 20 19 15 17 8L20 7C23 18 11 24 4 17Z",
        "more" => "M4 8A2 2 0 1 1 3.99 8Z M11 8A2 2 0 1 1 10.99 8Z M18 8A2 2 0 1 1 17.99 8Z",
        "arrow" => "M10 2L17 9L15 11L11 7V20H8V7L4 11L2 9Z",
        _ => "M10 1A9 9 0 1 1 9.99 1Z M8 8V17H11V8Z M8 4V6H11V4Z",
    });
}
