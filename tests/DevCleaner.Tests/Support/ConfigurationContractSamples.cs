namespace DevCleaner.Tests.Support;

public static class ConfigurationContractSamples
{
    public static TheoryData<string, string, bool> All => new()
    {
        {
            "case-insensitive names, category, unknown properties, and unrestricted marker",
            """
            {"SCHEMAVERSION":1,"futureRootOption":true,"CUSTOMRULES":[{
              "ID":"company.generated","CATEGORY":"bUiLd","PATTERNS":["**/.generated"],
              "MARKERS":["../accepted-marker"],"futureRuleOption":42
            }]}
            """,
            true
        },
        {
            "explicit false and null-normalized collections",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.explicit","category":"Dependency","patterns":["**/.packages"],"markers":null,"preselected":false
            }],"roots":null,"disabledRules":null}
            """,
            true
        },
        {
            "case-variant recognized duplicates use the effective last value",
            """
            {"SCHEMAVERSION":1,"schemaVersion":1,
             "ROOTS":["first-root"],"roots":["effective-root"],
             "CUSTOMRULES":[{"ID":"company.first","CATEGORY":"Build","PATTERNS":["**/first"]}],
             "customRules":[{"id":"company.effective","category":"Cache","patterns":["**/effective"]}]}
            """,
            true
        },
        {
            "preselected true",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["**/.generated"],"preselected":true
            }]}
            """,
            false
        },
        {
            "effective last case-variant customRules omits category",
            """
            {"schemaVersion":1,
             "CUSTOMRULES":[{"id":"company.first","category":"Build","patterns":["**/first"]}],
             "customRules":[{"id":"company.effective","patterns":["**/effective"]}]}
            """,
            false
        },
        {
            "missing category",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","patterns":["**/.generated"]
            }]}
            """,
            false
        },
        {
            "unsafe candidate pattern",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["../generated"]
            }]}
            """,
            false
        },
        {
            "empty marker",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["**/.generated"],"markers":[""]
            }]}
            """,
            false
        },
        {
            "whitespace-only custom rule id",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"  \t  ","category":"Build","patterns":["**/.generated"]
            }]}
            """,
            false
        },
        {
            "whitespace-only candidate pattern",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["  \t  "]
            }]}
            """,
            false
        },
        {
            "whitespace-only marker",
            """
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["**/.generated"],"markers":["  \t  "]
            }]}
            """,
            false
        },
    };
}
