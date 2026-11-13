// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using GraphitiNet.Models;

namespace GraphitiNet.Bench;

/// <summary>
/// A deterministic, mostly Arabic synthetic graph: entities, facts, episodes and communities spread over a few large
/// tenants and many small groups (one per person, org unit, project and site, as AbpAgent uses them). Words follow a
/// Zipf-Mandelbrot distribution (<c>1 / (rank + 1.5)</c>) over about 60,000 word forms, Arabic stop words first, so
/// <c>في</c>, <c>من</c>, <c>على</c> and the like occur in most rows. Record <c>i</c> of a kind is generated from its
/// own seed, so any slice of the corpus can be regenerated (and a load resumed) without the rest.
/// </summary>
internal sealed class Corpus
{
    public static readonly DateTimeOffset T0 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Arabic stop words and function words, most frequent first.</summary>
    public static readonly string[] ArabicStopWords =
    [
        "في", "من", "على", "و", "إلى", "أن", "التي", "الذي", "عن", "هذا", "مع", "ما", "لا", "كان", "هذه", "أو",
        "بين", "كل", "قد", "بعد", "ذلك", "عند", "ثم", "حتى", "لم", "هو", "هي", "كما", "إن", "لكن", "أي", "منذ",
        "خلال", "حيث", "لدى", "غير", "تم", "يتم", "أكثر", "فقط", "أيضا", "نحو", "عبر", "ضمن", "قبل", "بعض", "إذا",
        "لن", "ليس", "كانت", "يكون", "تكون", "وهو", "وهي", "هناك", "الى", "لقد", "بها", "به", "له", "لها", "فيه",
        "فيها", "منه", "عليه", "عليها", "الذين", "اللذين", "أنه", "أنها", "إنه", "وقد", "وفي", "ومن", "وعلى",
    ];

    private static readonly string[] ArabicContentWords =
    [
        "مشروع", "شركة", "عميل", "اجتماع", "تقرير", "موظف", "مدير", "فريق", "موقع", "عقد", "ميزانية", "خطة", "إدارة",
        "نظام", "بيانات", "طلب", "موعد", "أسبوع", "شهر", "سنة", "يوم", "مهمة", "مرحلة", "تسليم", "جودة", "صيانة",
        "مبنى", "طابق", "مكتب", "قسم", "وحدة", "منطقة", "مدينة", "عمل", "تطوير", "تصميم", "تنفيذ", "متابعة", "مراجعة",
        "اعتماد", "موافقة", "رفض", "تأجيل", "إلغاء", "تحديث", "تعديل", "إضافة", "حذف", "فاتورة", "دفعة", "مبلغ",
        "ريال", "دينار", "درهم", "جنيه", "تكلفة", "سعر", "عرض", "مناقصة", "مورد", "مقاول", "استشاري", "مهندس",
        "محاسب", "محامي", "طبيب", "معلم", "طالب", "مدرسة", "جامعة", "مستشفى", "عيادة", "وزارة", "هيئة", "بلدية",
        "حكومة", "قانون", "لائحة", "سياسة", "إجراء", "نموذج", "وثيقة", "ملف", "رسالة", "بريد", "هاتف", "جوال",
        "عنوان", "شارع", "حي", "طريق", "جسر", "محطة", "مطار", "ميناء", "سيارة", "شاحنة", "معدات", "أجهزة", "برنامج",
        "تطبيق", "خادم", "شبكة", "أمن", "حماية", "نسخة", "احتياطية", "تدريب", "دورة", "ورشة", "مؤتمر", "زيارة",
        "رحلة", "إجازة", "راتب", "مكافأة", "ترقية", "تقييم", "أداء", "هدف", "مؤشر", "نتيجة", "مخاطر", "مشكلة", "حل",
        "اقتراح", "قرار", "توصية", "ملاحظة", "سؤال", "جواب", "شكوى", "استفسار", "خدمة", "منتج", "مخزون", "مستودع",
        "شحنة", "توريد", "استلام", "فحص", "اختبار", "تشغيل", "إيقاف", "عطل", "إصلاح", "بلاغ", "حادث", "سلامة",
        "بيئة", "طاقة", "مياه", "كهرباء", "تكييف", "إنارة", "أرض", "مساحة", "متر", "مخطط", "رخصة", "تصريح", "عقار",
        "إيجار", "بيع", "شراء", "سوق", "عملاء", "مبيعات", "تسويق", "حملة", "إعلان", "منصة", "محتوى", "صورة", "فيديو",
        "جدول", "قائمة", "بند", "فقرة", "صفحة", "توقيع", "ختم", "أرشيف", "سجل", "حساب", "رصيد", "قرض", "بنك",
        "تحويل", "نقل", "انتقال", "تعيين", "استقالة", "مقابلة", "توظيف", "مرشح", "وظيفة", "مسؤول", "مشرف", "رئيس",
        "نائب", "عضو", "لجنة", "مجلس", "جمعية", "مؤسسة", "فرع", "إدارات", "أقسام", "مشاريع", "عقود", "تقارير",
        "اجتماعات", "موظفين", "عملائنا", "جديد", "جديدة", "كبير", "كبيرة", "صغير", "مهم", "مهمة", "عاجل", "نهائي",
        "أولي", "أول", "ثاني", "ثالث", "شهري", "سنوي", "يومي", "أسبوعي", "حالي", "سابق", "قادم", "متأخر", "مكتمل",
        "قال", "أكد", "أرسل", "طلب", "وافق", "حضر", "ناقش", "راجع", "أعد", "نفذ", "سلم", "استلم", "اتفق", "اقترح",
        "أضاف", "عدل", "ألغى", "أجل", "بدأ", "انتهى", "يعمل", "يحتاج", "يريد", "يجب", "يمكن", "سيتم", "تمت", "جرى",
        "أوضح", "أشار", "ذكر", "أبلغ", "طالب", "قرر", "أعلن", "وقع", "دفع", "حول", "نقل", "زار", "سافر", "عاد",
    ];

    private static readonly string[] Prefixes = ["", "ال", "و", "وال", "بال", "لل", "فال", "ب", "ل"];

    private static readonly string[] Suffixes = ["", "ات", "ين", "ون", "ه", "ها", "هم", "نا", "ي"];

    private static readonly string[] Patterns = ["{0}ا{1}{2}", "م{0}{1}و{2}", "ت{0}{1}ي{2}", "است{0}{1}ا{2}",
        "{0}{1}ا{2}ة", "ال{0}{1}ي{2}", "م{0}{1}{2}ة", "ي{0}{1}{2}", "ت{0}ا{1}{2}", "{0}{1}{2}ي"];

    private const string ArabicLetters = "ابتثجحخدذرزسشصضطظعغفقكلمنهوي";

    public static readonly string[] EnglishStopWords =
    [
        "the", "of", "and", "to", "in", "a", "is", "for", "on", "with", "that", "by", "at", "from", "as", "be", "this",
        "was", "are", "it", "an", "or", "will", "has", "have",
    ];

    private static readonly string[] EnglishContentWords =
    [
        "project", "meeting", "report", "client", "contract", "budget", "invoice", "server", "deadline", "review",
        "approval", "team", "manager", "site", "delivery", "system", "update", "request", "issue", "release",
        "design", "plan", "payment", "vendor", "license", "account", "network", "backup", "training", "schedule",
        "erp", "crm", "api", "sap", "oracle", "azure", "dashboard", "kpi", "sla", "hr",
    ];

    private static readonly string[] FirstNames =
    [
        "محمد", "أحمد", "علي", "عمر", "خالد", "عبدالله", "عبدالرحمن", "يوسف", "إبراهيم", "سعد", "فهد", "سلطان",
        "ناصر", "فيصل", "ماجد", "حسن", "حسين", "مصطفى", "طارق", "وليد", "فاطمة", "عائشة", "مريم", "نورة", "سارة",
        "هند", "ريم", "لينا", "أمل", "منى", "هدى", "رنا", "دانة", "لمى", "جود", "شهد", "Ahmed", "Sara", "John",
        "Maria",
    ];

    private static readonly string[] FamilyNames =
    [
        "العتيبي", "القحطاني", "الشمري", "الدوسري", "الغامدي", "الزهراني", "المطيري", "الحربي", "السبيعي",
        "العنزي", "الشهري", "المالكي", "الخالدي", "الأنصاري", "الحسيني", "النجار", "الحداد", "السيد", "عبدالعزيز",
        "منصور", "صالح", "حمدان", "سليمان", "الرشيد", "الفارس", "البكري", "التميمي", "الكعبي", "الهاشمي",
        "Hassan", "Smith",
    ];

    private static readonly string[] Cities =
    [
        "الرياض", "جدة", "الدمام", "مكة", "المدينة", "القاهرة", "الإسكندرية", "دبي", "أبوظبي", "عمان", "الدوحة",
        "الكويت", "المنامة", "مسقط", "بيروت", "تونس", "الرباط", "الخرطوم",
    ];

    private static readonly string[] Labels = ["Person", "Organization", "Project", "Place", "Document", "Event"];

    public static readonly string[] FactNames =
    [
        "WORKS_AT", "MANAGES", "LOCATED_IN", "PART_OF", "RESPONSIBLE_FOR", "ATTENDED", "SENT", "REQUESTED",
        "APPROVED", "REPORTS_TO", "MEMBER_OF", "OWNS", "SIGNED", "PAID", "DELIVERED", "SCHEDULED", "MENTIONED",
        "ASSIGNED_TO", "CREATED", "UPDATED", "VISITED", "SUPPLIES", "CONTRACTED_WITH", "DEPENDS_ON", "RELATES_TO",
    ];

    private static readonly string[] SourceDescriptions =
    [
        "رسالة واتساب", "بريد إلكتروني", "محضر اجتماع", "مكالمة هاتفية", "تقرير يومي", "chat", "email", "ticket",
    ];

    private readonly string[] _arabic;
    private readonly double[] _arabicCdf;
    private readonly string[] _english;
    private readonly double[] _englishCdf;
    private readonly double[] _contentCdf;
    private readonly int _firstContentRank;
    private readonly double[] _factNameCdf;

    public Corpus(Scale scale)
    {
        Scale = scale;
        _arabic = BuildArabicVocabulary(60_000);
        _arabicCdf = ZipfCdf(_arabic.Length, 1.5);
        _english = [.. EnglishStopWords, .. EnglishContentWords, .. Enumerable.Range(0, 3000).Select(PseudoEnglish)];
        _englishCdf = ZipfCdf(_english.Length, 1.5);
        _firstContentRank = ArabicStopWords.Length;
        _contentCdf = ZipfCdf(_arabic.Length - _firstContentRank, 1.5);
        _factNameCdf = ZipfCdf(FactNames.Length, 1);
        Groups = BuildGroups();
        var weights = Groups.Select(g => g.Weight).ToArray();
        _groupCdf = Cumulative(weights);
    }

    private readonly double[] _groupCdf;

    public Scale Scale { get; }

    public IReadOnlyList<GroupInfo> Groups { get; }

    /// <summary>The Arabic vocabulary, by rank (0 = commonest).</summary>
    public IReadOnlyList<string> ArabicVocabulary => _arabic;

    /// <summary>AbpAgent-like per-group languages: the second large tenant, projects and sites use <c>arabic</c>.</summary>
    public static string? TextSearchConfig(string groupId) =>
        groupId.StartsWith("org-beta", StringComparison.Ordinal)
        || groupId.StartsWith("project-", StringComparison.Ordinal)
        || groupId.StartsWith("site-", StringComparison.Ordinal)
            ? "arabic"
            : null;

    public string GroupOf(Kind kind, int index) => Groups[Sample(_groupCdf, Rng(kind, index, salt: 7))].Id;

    public static string Uuid(Kind kind, int index)
    {
        var bytes = new byte[16];
        new Random(Seed(kind, index, 99)).NextBytes(bytes);
        return new Guid(bytes).ToString();
    }

    public EntityNode Entity(int index)
    {
        var random = Rng(Kind.Entity, index);
        var label = Labels[random.Next(Labels.Length)];
        var name = label switch
        {
            "Person" => $"{Pick(FirstNames, random)} {Pick(FamilyNames, random)}",
            "Organization" => $"شركة {Pick(FamilyNames, random)} {ContentWord(random)}",
            "Project" => $"مشروع {ContentWord(random)} {Pick(Cities, random)}",
            "Place" => $"{Pick(Cities, random)} {ContentWord(random)}",
            "Document" => $"{ContentWord(random)} {ContentWord(random)} {random.Next(1, 999)}",
            _ => $"{ContentWord(random)} {ContentWord(random)}",
        };
        return new EntityNode
        {
            Uuid = Uuid(Kind.Entity, index),
            Name = name,
            GroupId = GroupOf(Kind.Entity, index),
            Labels = ["Entity", label],
            Summary = Text(random, 15 + random.Next(50)),
            CreatedAt = T0.AddMinutes(index),
        };
    }

    public EntityEdge Fact(int index)
    {
        var random = Rng(Kind.Fact, index);
        var group = GroupOf(Kind.Fact, index);
        var (source, target) = (EntityInGroup(group, random), EntityInGroup(group, random));
        var sourceName = source >= 0 ? Entity(source).Name : Pick(FirstNames, random);
        var targetName = target >= 0 ? Entity(target).Name : Pick(FirstNames, random);
        return new EntityEdge
        {
            Uuid = Uuid(Kind.Fact, index),
            GroupId = group,
            SourceNodeUuid = source >= 0 ? Uuid(Kind.Entity, source) : Uuid(Kind.Entity, 0),
            TargetNodeUuid = target >= 0 ? Uuid(Kind.Entity, target) : Uuid(Kind.Entity, 1),
            Name = FactNames[Sample(_factNameCdf, random)],
            Fact = $"{sourceName} {Text(random, 4 + random.Next(18))} {targetName}",
            ValidAt = T0.AddDays(random.Next(730)),
            InvalidAt = random.Next(10) == 0 ? T0.AddDays(730 + random.Next(100)) : null,
            CreatedAt = T0.AddMinutes(index),
        };
    }

    public EpisodicNode Episode(int index)
    {
        var random = Rng(Kind.Episode, index);
        var speaker = $"{Pick(FirstNames, random)} {Pick(FamilyNames, random)}";
        var source = (EpisodeType)(random.Next(10) switch { < 7 => 0, < 9 => 1, _ => 2 });
        var content = source == EpisodeType.Json
            ? $"{{\"from\": \"{speaker}\", \"subject\": \"{Text(random, 6)}\", \"body\": \"{Text(random, 30 + random.Next(80))}\"}}"
            : $"{speaker}: {Text(random, 40 + random.Next(200))}";
        return new EpisodicNode
        {
            Uuid = Uuid(Kind.Episode, index),
            Name = $"episode {index}",
            GroupId = GroupOf(Kind.Episode, index),
            Source = source,
            SourceDescription = Pick(SourceDescriptions, random),
            Content = content,
            ValidAt = T0.AddMinutes(index * 3),
            CreatedAt = T0.AddMinutes(index * 3),
        };
    }

    public CommunityNode Community(int index)
    {
        var random = Rng(Kind.Community, index);
        var name = string.Join(' ', Enumerable.Range(0, 2 + random.Next(4)).Select(_ => ContentWord(random)));
        return new CommunityNode
        {
            Uuid = Uuid(Kind.Community, index),
            Name = name,
            GroupId = GroupOf(Kind.Community, index),
            Summary = Text(random, 30),
            CreatedAt = T0.AddMinutes(index),
        };
    }

    /// <summary>A word of the given Arabic rank (0 = commonest).</summary>
    public string Word(int rank) => _arabic[rank];

    private int EntityInGroup(string group, Random random)
    {
        var members = EntitiesByGroup.Value.TryGetValue(group, out var list) ? list : null;
        return members is null || members.Count == 0 ? -1 : members[random.Next(members.Count)];
    }

    private Lazy<Dictionary<string, List<int>>> EntitiesByGroup => _entitiesByGroup ??= new(() =>
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < Scale.Entities; i++)
        {
            var group = GroupOf(Kind.Entity, i);
            if (!map.TryGetValue(group, out var list))
            {
                map[group] = list = [];
            }

            list.Add(i);
        }

        return map;
    });

    private Lazy<Dictionary<string, List<int>>>? _entitiesByGroup;

    private string Text(Random random, int words)
    {
        var parts = new string[words];
        for (var i = 0; i < words; i++)
        {
            parts[i] = random.Next(100) < 6 ? _english[Sample(_englishCdf, random)] : _arabic[Sample(_arabicCdf, random)];
        }

        return string.Join(' ', parts);
    }

    private string ContentWord(Random random) => _arabic[_firstContentRank + Sample(_contentCdf, random)];

    private static string Pick(string[] values, Random random) => values[random.Next(values.Length)];

    private static List<GroupInfo> BuildGroups()
    {
        var groups = new List<GroupInfo>
        {
            new("org-alpha", 0.22),
            new("org-beta", 0.15),
            new("org-gamma", 0.08),
        };

        // The rest is spread over many small groups, a few of them bigger than the others.
        var small = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            small.Add($"person-{i:D3}");
        }

        for (var i = 0; i < 50; i++)
        {
            small.Add($"unit-{i:D2}");
            small.Add($"site-{i:D2}");
        }

        for (var i = 0; i < 100; i++)
        {
            small.Add($"project-{i:D3}");
        }

        var weights = small.Select((_, i) => 1.0 / Math.Pow(1 + (i * 7 % small.Count), 0.6)).ToArray();
        var total = weights.Sum();
        groups.AddRange(small.Select((g, i) => new GroupInfo(g, 0.55 * weights[i] / total)));
        return groups;
    }

    private static string[] BuildArabicVocabulary(int size)
    {
        var words = new List<string>(size);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string w)
        {
            if (words.Count < size && seen.Add(w))
            {
                words.Add(w);
            }
        }

        foreach (var w in ArabicStopWords)
        {
            Add(w);
        }

        // Content words, then their inflected and prefixed forms, interleaved so that common words come first.
        foreach (var prefix in Prefixes)
        {
            foreach (var suffix in Suffixes.Take(prefix.Length == 0 ? 1 : 1))
            {
                foreach (var w in ArabicContentWords)
                {
                    Add(prefix + w + suffix);
                }
            }
        }

        foreach (var suffix in Suffixes.Skip(1))
        {
            foreach (var prefix in Prefixes)
            {
                foreach (var w in ArabicContentWords)
                {
                    Add(prefix + w + suffix);
                }
            }
        }

        // The long tail: words from pseudo-roots in common Arabic patterns.
        var random = new Random(1234);
        while (words.Count < size)
        {
            var root = new[]
            {
                ArabicLetters[random.Next(ArabicLetters.Length)],
                ArabicLetters[random.Next(ArabicLetters.Length)],
                ArabicLetters[random.Next(ArabicLetters.Length)],
            };
            var pattern = Patterns[random.Next(Patterns.Length)];
            Add(Prefixes[random.Next(3)] + string.Format(CultureInfo.InvariantCulture, pattern, root[0], root[1], root[2]));
        }

        // Shuffle the variants a little within bands, so ranks are not ordered by prefix.
        var shuffle = new Random(4321);
        for (var start = ArabicStopWords.Length; start < words.Count; start += 50)
        {
            var end = Math.Min(start + 50, words.Count);
            for (var i = end - 1; i > start; i--)
            {
                var j = start + shuffle.Next(i - start + 1);
                (words[i], words[j]) = (words[j], words[i]);
            }
        }

        return [.. words];
    }

    private static string PseudoEnglish(int i)
    {
        const string consonants = "bcdfghklmnprstvw";
        const string vowels = "aeiou";
        var random = new Random(i + 77);
        var length = 2 + random.Next(3);
        var chars = new List<char>();
        for (var s = 0; s < length; s++)
        {
            chars.Add(consonants[random.Next(consonants.Length)]);
            chars.Add(vowels[random.Next(vowels.Length)]);
        }

        return new string([.. chars]);
    }

    private static double[] ZipfCdf(int n, double q)
    {
        var weights = new double[n];
        for (var r = 0; r < n; r++)
        {
            weights[r] = 1.0 / (r + 1 + q);
        }

        return Cumulative(weights);
    }

    private static double[] Cumulative(double[] weights)
    {
        var cdf = new double[weights.Length];
        var total = weights.Sum();
        var acc = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            acc += weights[i] / total;
            cdf[i] = acc;
        }

        cdf[^1] = 1.0;
        return cdf;
    }

    private static int Sample(double[] cdf, Random random)
    {
        var index = Array.BinarySearch(cdf, random.NextDouble());
        return index >= 0 ? index : Math.Min(~index, cdf.Length - 1);
    }

    private static Random Rng(Kind kind, int index, int salt = 0) => new(Seed(kind, index, salt));

    private static int Seed(Kind kind, int index, int salt)
    {
        // SplitMix64 of (kind, index, salt): stable across runs and processes, unlike string.GetHashCode.
        var z = ((ulong)kind * 0x9E3779B97F4A7C15UL) ^ ((ulong)index * 0xBF58476D1CE4E5B9UL) ^ ((ulong)salt * 0x94D049BB133111EBUL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z & 0x7FFFFFFF);
    }
}

internal enum Kind
{
    Entity = 1,
    Fact = 2,
    Episode = 3,
    Community = 4,
}

internal sealed record GroupInfo(string Id, double Weight);

/// <summary>How many records of each kind.</summary>
internal sealed record Scale(int Entities, int Facts, int Episodes, int Communities)
{
    public int Total => Entities + Facts + Episodes + Communities;

    public int Count(Kind kind) => kind switch
    {
        Kind.Entity => Entities,
        Kind.Fact => Facts,
        Kind.Episode => Episodes,
        _ => Communities,
    };

    /// <summary>The default: about one million records.</summary>
    public static Scale Million { get; } = new(300_000, 550_000, 140_000, 10_000);

    public static Scale Of(double factor) => new(
        (int)(Million.Entities * factor),
        (int)(Million.Facts * factor),
        (int)(Million.Episodes * factor),
        (int)(Million.Communities * factor));
}
