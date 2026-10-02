using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class StandardParameterTests
{
    private const string A = "2c596d00-617a-4474-adb0-e6b66763695d";
    private const string B = "4dad6601-cd2c-42e8-bf24-8d2d7e89053f";
    private static string Fop(string rows) => "# Revit FOP\n*GROUP\tID\tNAME\nGROUP\t5\tАрматура\n" +
        "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\n" + rows;

    [Fact]
    public void PreservesLegacySpecsAndDescriptionsAndEmptyCells()
    {
        var parsed = SharedParameterCatalog.Parse(Fop($"PARAM\t{A}\tМасса\tNUMBER\t\t5\t1\tМасса в кг\t1"), "source.txt", "test");
        var d = Assert.Single(parsed.Definitions);
        Assert.Equal("NUMBER", d.DataType);
        Assert.Equal("", d.DataCategory);
        Assert.Equal("Арматура", d.Group);
        Assert.Equal("Масса в кг", d.Description);
        Assert.Equal(Guid.Parse(A), d.Guid);
        Assert.True(d.Visible);
        Assert.True(d.UserModifiable);
        Assert.Null(d.HideWhenNoValue);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void UsesColumnHeaderInsteadOfAssumingFieldOrder()
    {
        var parsed = SharedParameterCatalog.Parse($"*PARAM\tNAME\tDATATYPE\tGUID\tGROUP\nPARAM\tTest\tLENGTH\t{A}\t9\nGROUP\t9\tLater group", "source", "test");
        Assert.Equal("Later group", Assert.Single(parsed.Definitions).Group);
    }

    [Fact]
    public void DiagnosesInvalidRowsDuplicateGuidsAndSameNameDifferentIdentity()
    {
        var parsed = SharedParameterCatalog.Parse(Fop($"PARAM\tbad\tName\tTEXT\nPARAM\t{A}\tName\tTEXT\nPARAM\t{A}\tDifferent\tTEXT\nPARAM\t{B}\tName\tNUMBER"), "source", "test");
        Assert.Equal(2, parsed.Definitions.Count);
        Assert.Equal(3, parsed.Warnings.Count);
        Assert.Equal("Name", parsed.Definitions.First().Name);
    }

    [Fact]
    public void RejectsArbitraryTextAndOversizedInput()
    {
        Assert.Throws<InvalidDataException>(() => SharedParameterCatalog.Parse("some arbitrary text", "source", "test"));
        Assert.Throws<InvalidDataException>(() => SharedParameterCatalog.Parse(new string('x', SharedParameterCatalog.MaxBytes + 1), "source", "test"));
    }

    [Fact]
    public void ReadsUtf16FopWithoutModifyingItAndHashesOriginalBytes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ClaudeRevit-fop-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "ФОП2021.txt");
            File.WriteAllText(path, Fop($"PARAM\t{A}\tADSK_Масса\tMASS"), Encoding.Unicode);
            var before = File.ReadAllBytes(path);
            var parsed = SharedParameterCatalog.Read(path);
            Assert.Equal("ADSK_Масса", Assert.Single(parsed.Definitions).Name);
            Assert.Equal(64, parsed.Sha256.Length);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void RenamedParameterMatchesByGuidAndSameNameWrongGuidDoesNot()
    {
        var reference = SharedParameterCatalog.Parse(Fop($"PARAM\t{A}\tRussianName\tTEXT"), "source", "test").Definitions;
        var matched = StandardParameterMatching.Detect(new[] { ((Guid?)Guid.Parse(A), "EnglishName") }, reference);
        Assert.Equal(1, Assert.Single(matched).MatchedGuidCount);
        Assert.Empty(StandardParameterMatching.Detect(new[] { ((Guid?)Guid.Parse(B), "RussianName") }, reference));
    }

    [Fact]
    public void AdskPrefixIsOnlyAHintNotVerifiedEdition()
    {
        var evidence = Assert.Single(StandardParameterMatching.Detect(new[] { ((Guid?)null, "ADSK_Назначение вида") }, Array.Empty<SharedParameterDefinition>()));
        Assert.Equal(0, evidence.MatchedGuidCount);
        Assert.Equal(1, evidence.NameHints);
        Assert.Contains("name-hint-only", evidence.Confidence);
    }

    [Fact]
    public void BundledReferenceContainsActualRuEngAliasesAndOriginalMassType()
    {
        var bimstarter = StandardKnowledge.Definitions.Where(d => d.Profile.StartsWith("BIMStarter")).ToList();
        Assert.Equal(533, bimstarter.Count);
        Assert.Equal(313, bimstarter.Select(d => d.Guid).Distinct().Count());
        var aliases = bimstarter.Where(d => d.Guid == Guid.Parse("32989501-0d17-4916-8777-da950841c6d7")).ToList();
        Assert.Contains(aliases, d => d.Name == "О_Масса" && d.DataType == "NUMBER");
        Assert.Contains(aliases, d => d.Name == "Cmn_Weight" && d.DataType == "NUMBER");
        Assert.All(aliases, d => Assert.Equal("BIMStarter2020-ADSK-imports", d.Profile));
    }
    [Fact]
    public void AdskEditionsPreserveGuidsAndOriginalSpecs()
    {
        var editions = StandardKnowledge.Definitions.Where(d => d.Profile.StartsWith("ADSK-")).ToList();
        Assert.Equal(264, editions.Count(d => d.Profile == "ADSK-2019"));
        Assert.Equal(323, editions.Count(d => d.Profile == "ADSK-2021"));
        Assert.Equal(323, editions.Select(d => d.Guid).Distinct().Count());
        Assert.All(editions.GroupBy(d => d.Guid), g => Assert.Single(g.Select(d => d.DataType).Distinct()));
        Assert.Equal(1120, StandardKnowledge.Definitions.Count);
        Assert.Contains(editions, d => d.RecommendedGroup != null);
        Assert.All(editions, d => Assert.NotNull(d.Visible));
    }
}
