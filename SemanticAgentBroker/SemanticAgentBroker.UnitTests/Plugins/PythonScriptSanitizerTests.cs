using SemanticAgentBroker.Plugins.BuiltIn.Required;

namespace SemanticAgentBroker.UnitTests.Plugins;

[TestClass]
public class PythonScriptSanitizerTests
{
    [TestMethod]
    public void Sanitize_WhenNullOrEmpty_ReturnsSame()
    {
        Assert.AreEqual(string.Empty, PythonScriptSanitizer.Sanitize(null));
        Assert.AreEqual(string.Empty, PythonScriptSanitizer.Sanitize(""));
        Assert.AreEqual("   ", PythonScriptSanitizer.Sanitize("   "));
    }

    [TestMethod]
    public void Sanitize_WhenBlockHeaderMissingColon_AppendsColon()
    {
        var input = """
            import pandas as pd
            df = pd.read_csv('test.csv')
            for _, row in df.iterrows()
                print(row)
            if not os.path.exists('file.txt')
                print('missing')
            else
                print('exists')
            try
                x = 1 / 0
            except Exception as e
                print(e)
            finally
                print('done')
            """;

        var sanitized = PythonScriptSanitizer.Sanitize(input);

        StringAssert.Contains(sanitized, "for _, row in df.iterrows():");
        StringAssert.Contains(sanitized, "if not os.path.exists('file.txt'):");
        StringAssert.Contains(sanitized, "else:");
        StringAssert.Contains(sanitized, "try:");
        StringAssert.Contains(sanitized, "except Exception as e:");
        StringAssert.Contains(sanitized, "finally:");
    }

    [TestMethod]
    public void Sanitize_WhenBlockHeaderHasTrailingComment_AppendsColonBeforeComment()
    {
        var input = "for _, row in df.iterrows() # iterate over rows";
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        Assert.AreEqual("for _, row in df.iterrows(): # iterate over rows", sanitized);
    }

    [TestMethod]
    public void Sanitize_WhenBlockHeaderAlreadyHasColon_DoesNotDuplicateColon()
    {
        var input = "for _, row in df.iterrows():\n    print(row)";
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        Assert.AreEqual(input, sanitized);
    }

    [TestMethod]
    public void Sanitize_WhenFileOpenModeIsN_CorrectsToW()
    {
        var input1 = "with open(report_file, 'n', encoding='utf-8') as f:";
        var sanitized1 = PythonScriptSanitizer.Sanitize(input1);
        Assert.AreEqual("with open(report_file, 'w', encoding='utf-8') as f:", sanitized1);

        var input2 = "f = open('data.csv', \"n\")";
        var sanitized2 = PythonScriptSanitizer.Sanitize(input2);
        Assert.AreEqual("f = open('data.csv', 'w')", sanitized2);

        var input3 = "f = open('data.csv', mode='n')";
        var sanitized3 = PythonScriptSanitizer.Sanitize(input3);
        Assert.AreEqual("f = open('data.csv', mode='w')", sanitized3);
    }

    [TestMethod]
    public void Sanitize_WhenLoopTargetHasTokenStutter_NormalizesIdentifier()
    {
        var input = "for s in filtered 'filtered_sensors':\n    print(s)";
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        StringAssert.Contains(sanitized, "for s in filtered_sensors:");
    }

    [TestMethod]
    public void Sanitize_WhenFStringHasAccidentalQuoteDotVariable_CleansInterpolation()
    {
        var input = "print(f'Metadata saved to {\'.metadata_file\'}')";
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        Assert.AreEqual("print(f'Metadata saved to {metadata_file}')", sanitized);
    }

    [TestMethod]
    public void Sanitize_WhenSingleQuotedFStringHasInnerSingleQuotes_NormalizesToDoubleQuotes()
    {
        var input = "print(f'Value is {row[\'val\']}')";
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        Assert.AreEqual("print(f'Value is {row[\"val\"]}')", sanitized);
    }

    [TestMethod]
    public void Sanitize_WhenMultiLineUnclosedHeader_DoesNotAppendPrematureColon()
    {
        // Delimiters on line 1 are unbalanced (open paren not closed)
        var input = """
            if (a and
                b):
                print('ok')
            """;
        var sanitized = PythonScriptSanitizer.Sanitize(input);
        Assert.AreEqual(input, sanitized);
    }
}
