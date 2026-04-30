using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChBrowser.Services.Api;

/// <summary>bbsmenu.json デシリアライズ用 DTO。</summary>
internal sealed class BbsmenuJsonDto
{
    [JsonPropertyName("last_modify")]        public long?   LastModify        { get; set; }
    [JsonPropertyName("last_modify_string")] public string? LastModifyString  { get; set; }
    [JsonPropertyName("description")]        public string? Description      { get; set; }
    [JsonPropertyName("menu_list")]          public List<MenuListEntryDto>? MenuList { get; set; }
}

internal sealed class MenuListEntryDto
{
    [JsonPropertyName("category_name")]    public string? CategoryName   { get; set; }
    [JsonPropertyName("category_number")]  public int?    CategoryNumber { get; set; }
    [JsonPropertyName("category_total")]   public int?    CategoryTotal  { get; set; }
    [JsonPropertyName("category_content")] public List<BoardEntryDto>? CategoryContent { get; set; }
}

internal sealed class BoardEntryDto
{
    [JsonPropertyName("url")]            public string? Url           { get; set; }
    [JsonPropertyName("board_name")]     public string? BoardName     { get; set; }
    [JsonPropertyName("directory_name")] public string? DirectoryName { get; set; }
    [JsonPropertyName("category")]       public int?    Category      { get; set; }
    [JsonPropertyName("category_name")]  public string? CategoryName  { get; set; }
    [JsonPropertyName("category_order")] public int?    CategoryOrder { get; set; }
}
