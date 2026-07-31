namespace OmniRef.Core.Models;

public sealed record ItemStyle(
    string Background = "#FF252932",
    string Foreground = "#FFF5F7FA",
    string Accent = "#FF7C8CFF",
    double CornerRadius = 10,
    double Opacity = 1);
