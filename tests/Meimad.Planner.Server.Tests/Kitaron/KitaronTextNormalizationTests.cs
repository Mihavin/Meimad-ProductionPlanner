using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronTextNormalizationTests
{
    [Fact]
    public void Bidi_wrapped_and_plain_part_numbers_normalize_to_the_same_value()
    {
        // Reproduces a real duplicate: Kitaron sent "30P450172000-501" for one row and
        // "‎30P450172000-501‏" (wrapped in LRM/RLM) for another, so the two synced as
        // separate Cases because the raw strings differed even though the part is identical.
        const string plain = "30P450172000-501";
        const string wrapped = "‎30P450172000-501‏";

        Assert.Equal(plain, KitaronTextNormalization.Clean(wrapped));
        Assert.Equal(
            KitaronTextNormalization.Clean(plain),
            KitaronTextNormalization.Clean(wrapped),
            StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("​value")]
    [InlineData("‎value")]
    [InlineData("‏value")]
    [InlineData("؜value")]
    [InlineData("‪value‬")]
    [InlineData("‫value‬")]
    [InlineData("‭value‬")]
    [InlineData("‮value‬")]
    [InlineData("⁦value⁩")]
    [InlineData("⁧value⁩")]
    [InlineData("⁨value⁩")]
    [InlineData("﻿value")]
    public void Strips_invisible_bidi_and_format_marks(string wrapped)
    {
        Assert.Equal("value", KitaronTextNormalization.Clean(wrapped));
    }

    [Fact]
    public void Ordinary_whitespace_is_still_trimmed()
    {
        Assert.Equal("value", KitaronTextNormalization.Clean("  value  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("‎‏")]
    public void Empty_or_mark_only_input_becomes_null(string? value)
    {
        Assert.Null(KitaronTextNormalization.Clean(value));
    }

    [Fact]
    public void CleanRequired_falls_back_to_the_trimmed_original_when_cleaning_would_empty_it()
    {
        Assert.Equal("‎‏", KitaronTextNormalization.CleanRequired("‎‏"));
        Assert.Equal("30P450172000-501", KitaronTextNormalization.CleanRequired("‎30P450172000-501‏"));
    }
}
