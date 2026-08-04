using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Soruda geçip plana yansımayan koşulları yakalayan kontrol.
//
// Bu testler bir hatayı değil, SESSİZ YANLIŞ CEVABI önlüyor: "2023 yılında toplam satış"
// sorusuna filtresiz bir plan üretilirse sorgu çalışır, grafik çizilir ve kullanıcı bütün
// yılların toplamına bakıp onu 2023 sanır. Hata mesajı görmediği için de fark etmez.
public class QueryPlanValidationTests
{
    private static QueryPlan Plan(params PlanFilter[] filters) =>
        new(Kind: "aggregate", From: "Satislar", Filters: filters,
            Metrics: new[] { new PlanMetric("sum", "tutar") });

    [Theory]
    [InlineData("2023 yılında toplam satış ne kadar")]
    [InlineData("2022 cirosu")]
    [InlineData("1998 yılındaki kayıtlar")]
    [InlineData("2030 hedefi tutturuldu mu")]
    public void QuestionWithYear_ButNoDateCondition_Throws(string question)
    {
        // Yıl kontrolü tek bir yıla bağlı DEĞİL: 1900-2099 arası her yıl tetikler.
        var ex = Assert.Throws<InvalidQueryException>(() =>
            QueryPlanMapper.ValidateAgainstQuestion(Plan(), question));

        Assert.Contains("tarih koşulu üretilemedi", ex.Message);
    }

    [Fact]
    public void ErrorMessage_NamesTheYearFromTheQuestion()
    {
        var ex = Assert.Throws<InvalidQueryException>(() =>
            QueryPlanMapper.ValidateAgainstQuestion(Plan(), "2022 yılında kaç satış oldu"));

        Assert.Contains("2022", ex.Message);
    }

    [Fact]
    public void QuestionWithYear_AndAbsoluteDateRange_IsAccepted()
    {
        var plan = Plan(
            new PlanFilter(Column: "tarih", Op: "gte", Value: "2023-01-01"),
            new PlanFilter(Column: "tarih", Op: "lt", Value: "2024-01-01"));

        QueryPlanMapper.ValidateAgainstQuestion(plan, "2023 yılında toplam satış");
    }

    [Fact]
    public void QuestionWithYear_AndRelativePeriod_IsAccepted()
    {
        // Kullanıcı "2026 (bu yıl)" gibi yazmış olabilir; model göreli etiket seçerse
        // bu doğru bir plandır ve engellenmemeli.
        var plan = Plan(new PlanFilter(Column: "tarih", Op: "inPeriod", Value: "buYil"));

        QueryPlanMapper.ValidateAgainstQuestion(plan, "2026 yılında toplam satış");
    }

    [Fact]
    public void DateConditionInsideOrGroup_IsFound()
    {
        // Koşul VEYA grubunun içinde de olabilir; arama ağacın tamamında yapılmalı.
        var plan = Plan(new PlanFilter(
            Logic: "or",
            Children: new[]
            {
                new PlanFilter(Column: "sehir", Op: "eq", Value: "Ankara"),
                new PlanFilter(Column: "tarih", Op: "gte", Value: "2023-01-01"),
            }));

        QueryPlanMapper.ValidateAgainstQuestion(plan, "2023 yılında Ankara satışları");
    }

    [Fact]
    public void ComparisonPlan_IsAccepted()
    {
        // Dönem karşılaştırması zaten tarihe dayanır; ayrıca filtre aramaya gerek yok.
        var plan = new QueryPlan(
            Kind: "aggregate", From: "Satislar",
            Metrics: new[] { new PlanMetric("sum", "tutar") },
            Compare: new PlanCompare("tarih", "buYil", "gecenYil"));

        QueryPlanMapper.ValidateAgainstQuestion(plan, "2026 ile 2025 karşılaştırması");
    }

    [Fact]
    public void QuestionWithoutYear_IsNeverBlocked()
    {
        QueryPlanMapper.ValidateAgainstQuestion(Plan(), "Şehirlere göre toplam satış");
    }

    [Fact]
    public void NumberThatIsNotAYear_DoesNotTrigger()
    {
        // "1000 TL üstü" bir yıl değil; kontrol bunu tarih koşulu saymamalı.
        QueryPlanMapper.ValidateAgainstQuestion(
            Plan(new PlanFilter(Column: "tutar", Op: "gt", Value: "1000")),
            "1000 TL üstü satışlar");
    }
}
