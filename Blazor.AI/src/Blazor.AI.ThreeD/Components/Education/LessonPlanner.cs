namespace Blazor.AI.ThreeD;

/// <summary>Offline lesson outlines so the classroom works without a model; a few topics are written out in full.</summary>
public static class LessonPlanner
{
    public static AIClassroom3D.Lesson Local(string topic)
    {
        var t = topic.Trim();
        if (t.Contains("photosynth", StringComparison.OrdinalIgnoreCase) || t.Contains("fotosintesis", StringComparison.OrdinalIgnoreCase))
        {
            return new AIClassroom3D.Lesson("Photosynthesis",
            [
                "Plants make their own food using light energy from the Sun.",
                "Chlorophyll in the leaves absorbs light, which is why leaves look green.",
                "Carbon dioxide and water are turned into glucose and oxygen.",
                "The oxygen we breathe is released by plants as a by-product.",
            ],
            [
                new("Which gas do plants take in for photosynthesis?", ["Oxygen", "Carbon dioxide", "Nitrogen", "Hydrogen"], 1),
                new("What gives leaves their green colour?", ["Glucose", "Water", "Chlorophyll", "Starch"], 2),
                new("What do plants release into the air?", ["Oxygen", "Carbon dioxide", "Smoke", "Helium"], 0),
            ]);
        }

        if (t.Contains("volcan", StringComparison.OrdinalIgnoreCase) || t.Contains("gunung", StringComparison.OrdinalIgnoreCase))
        {
            return new AIClassroom3D.Lesson("Volcanoes",
            [
                "A volcano is an opening where molten rock, gas and ash escape from inside the Earth.",
                "Indonesia sits on the Pacific Ring of Fire and has about 130 active volcanoes.",
                "Magma that reaches the surface is called lava.",
                "Volcanic ash makes soil very fertile, which helps farmers grow rice.",
            ],
            [
                new("What is magma called when it reaches the surface?", ["Ash", "Lava", "Basalt", "Steam"], 1),
                new("Which belt of volcanoes does Indonesia sit on?", ["Ring of Fire", "Sahara belt", "Arctic circle", "Andes line"], 0),
            ]);
        }

        return new AIClassroom3D.Lesson(System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.Length == 0 ? "Today's topic" : t),
        [
            $"What {t} is, in one sentence.",
            $"Why {t} matters in everyday life.",
            $"How {t} works, step by step.",
            $"A real example of {t} you can see around you.",
        ],
        [
            new($"Which statement best describes {t}?", ["A key idea we studied today", "Something unrelated", "A kind of food", "A type of weather"], 0),
        ]);
    }
}
