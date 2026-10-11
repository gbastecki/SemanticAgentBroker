using SemanticAgentBroker.Api.Features.ReAct.Services;

namespace SemanticAgentBroker.UnitTests.Api.ReAct;

[TestClass]
public class JsonSanitizerTests
{
    [TestMethod]
    [DataRow("{\"key\": \"value\", \"count\": 42}", false)]
    [DataRow("{\"nested\": {\"flag\": true, \"items\": [1, 2, 3]}}", false)]
    [DataRow("[]", false)]
    [DataRow("{}", false)]
    public void TrySanitize_ValidJson_ReturnsOriginalWithoutModifications(string input, bool expectedWasSanitized)
    {
        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(input);

        // Assert
        Assert.AreEqual(expectedWasSanitized, wasSanitized);
        Assert.IsNull(warning);
        Assert.AreEqual(input, sanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void TrySanitize_NullOrWhitespace_ReturnsEmptyJsonObject(string? input)
    {
        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(input!);

        // Assert
        Assert.AreEqual("{}", sanitized);
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    public void TrySanitize_SmartQuotes_NormalizesQuotesAndParsesSuccessfully()
    {
        // Arrange - contains Unicode curly quotes: “ ”
        string raw = "{\u201Ccommand\u201D: \u201Cecho hello world\u201D}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "Smart quotes normalized");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
        StringAssert.Contains(sanitized, "\"command\": \"echo hello world\"");
    }

    [TestMethod]
    public void TrySanitize_SingleQuotedJson_RepairsToValidDoubleQuotedJson()
    {
        // Arrange - Python style single-quoted dict
        string raw = "{'filename': 'data.csv', 'limit': 100, 'enabled': true}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "Single-quoted JSON repaired");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
        StringAssert.Contains(sanitized, "\"filename\": \"data.csv\"");
    }

    [TestMethod]
    public void TrySanitize_UrlEncodedCharacters_DecodesStructuralCharacters()
    {
        // Arrange - contains URL encoded space (%20) and newline (%0A) inside single quoted JSON
        string raw = "{'command': 'echo%20hello%0Aworld'}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "URL-encoded characters decoded");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
        StringAssert.Contains(sanitized, "\"command\": \"echo hello\\nworld\"");
    }

    [TestMethod]
    public void TrySanitize_LiteralControlCharactersInsideStrings_EscapesNewlinesAndTabs()
    {
        // Arrange - literal unescaped newlines and tabs inside a JSON string value
        string raw = "{\n  \"script\": \"def foo():\n\treturn 42\n\"\n}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    [DataRow("{\"items\": [1, 2, 3,], \"name\": \"test\",}")]
    [DataRow("{\"a\": 1, \"b\": 2,}")]
    public void TrySanitize_TrailingCommas_RemovesTrailingCommas(string raw)
    {
        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "Trailing commas removed");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    [DataRow("{\"action\": \"run\", \"params\": {\"id\": 123", true)]
    [DataRow("{\"data\": [\"a\", \"b\"", true)]
    public void TrySanitize_UnbalancedBrackets_AppendsMissingClosers(string raw, bool expectRepair)
    {
        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "Missing brackets added");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    public void TrySanitize_UnescapedQuotesInSingleKeyScript_RepairsQuotes()
    {
        // Arrange - LLM generated python script with unescaped double quotes inside value
        string raw = "{\"script\": \"import os; print(\"hello\")\"}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }

    [TestMethod]
    public void TrySanitize_HeuristicFallback_ExtractsKeyValuePairsFromLooseText()
    {
        // Arrange - loose pseudo-JSON produced by small LLM
        string raw = "\"filename\": \"output.txt\", \"content\": \"hello world\"";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
        StringAssert.Contains(sanitized, "\"filename\"");
        StringAssert.Contains(sanitized, "\"content\"");
    }

    [TestMethod]
    public void RepairSingleQuotedJson_PreservesDoubleQuotesInsideSingleQuotedStrings()
    {
        // Arrange
        string raw = "{'message': 'He said \"Hello\"'}";

        // Act
        string repaired = JsonSanitizer.RepairSingleQuotedJson(raw);

        // Assert
        Assert.IsTrue(JsonSanitizer.IsValidJson(repaired));
        StringAssert.Contains(repaired, "\\\"Hello\\\"");
    }

    [TestMethod]
    public void NormalizeUnicodeAndByteArtifacts_ReplacesNonBreakingSpacesAndZeroWidthChars()
    {
        // Arrange - contains NBSP (\u00A0), Narrow NBSP (\u202F), Zero-Width Space (\u200B), and BOM (\uFEFF)
        string raw = "import\u00A0pandas\u202Fas\u200B\uFEFFpd";

        // Act
        string normalized = JsonSanitizer.NormalizeUnicodeAndByteArtifacts(raw);

        // Assert
        Assert.AreEqual("import pandas as pd", normalized);
    }

    [TestMethod]
    public void NormalizeUnicodeAndByteArtifacts_RepairsCorruptedWorkspacePaths()
    {
        // Arrange - contains /app/<0xC2>dir and /app/\u00C2dir
        string raw1 = "{\"script\": \"df = pd.read_csv('/app/<0xC2>dir/data.csv')\"}";
        string raw2 = "{\"script\": \"df = pd.read_csv('/app/\u00C2dir/data.csv')\"}";

        // Act
        string normalized1 = JsonSanitizer.NormalizeUnicodeAndByteArtifacts(raw1);
        string normalized2 = JsonSanitizer.NormalizeUnicodeAndByteArtifacts(raw2);

        // Assert
        StringAssert.Contains(normalized1, "/app/workdir/data.csv");
        StringAssert.Contains(normalized2, "/app/workdir/data.csv");
    }

    [TestMethod]
    public void TrySanitize_JsonWithNonBreakingSpacesAndCorruptedPath_NormalizesAndValidates()
    {
        // Arrange
        string raw = "{\u00A0\"script\":\u00A0\"import\u00A0json; path = '/app/<0xC2>dir/output.json'\"\u00A0}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
        StringAssert.Contains(sanitized, "/app/workdir/output.json");
    }

    [TestMethod]
    public void TrySanitize_UrlEncodedQuotesInKeys_DecodesToValidJson()
    {
        // Arrange
        string raw = "{%22action%22: %22run%22}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON but got: {sanitized}");
        StringAssert.Contains(sanitized, "\"action\": \"run\"");
    }

    [TestMethod]
    public void TrySanitize_FullyUrlEncodedJson_DecodesToValidJson()
    {
        // Arrange
        string raw = "%7B%22thought%22%3A%20%22test%22%7D";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON but got: {sanitized}");
        StringAssert.Contains(sanitized, "\"thought\": \"test\"");
    }

    [TestMethod]
    public void TrySanitize_UrlEncodedQuotesInsideSingleQuotedValues_DecodesAndParsesValidJson()
    {
        // Arrange
        string raw = "{'command': 'echo %22hello%22'}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON but got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_NestedObjectInsideArray_AppendsClosingBracketsInLifoOrder()
    {
        // Arrange: Array containing an object with nested object, truncated
        string raw = "[{\"action\": \"test\", \"params\": {\"key\": 123";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        StringAssert.StartsWith(sanitized, "[", $"Expected array to remain an array, got: {sanitized}");
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON with proper LIFO closing delimiters, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_UnclosedStringValue_AppendsQuoteAndClosingBrackets()
    {
        // Arrange: Truncated JSON inside a string value
        string raw = "{\"action\": \"test\", \"script\": \"print('hello')\n";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON with closed string and object, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_DeeplyNestedArraysAndObjects_CorrectlyBalances()
    {
        // Arrange: Array -> Object -> Array -> Object
        string raw = "[{\"steps\": [{\"id\": 1";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_SingleQuotedJsonWithEscapedSingleQuote_RepairsToValidDoubleQuotedJson()
    {
        // Arrange - Python dict with escaped single quote
        string raw = "{'message': 'it\\'s a test', 'action': 'run'}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"it's a test\"");
    }

    [TestMethod]
    public void TrySanitize_StringLiteralContainingCommaBeforeBracket_PreservesCommaInsideString()
    {
        // Arrange - malformed JSON requiring sanitization (single quotes + trailing comma)
        // containing string literals with comma followed by bracket/brace
        string raw = "{'pattern': '[a,]', 'sql': 'SELECT 1, }', 'items': [1, 2,],}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"[a,]\"");
        StringAssert.Contains(sanitized, "\"SELECT 1, }\"");
        StringAssert.Contains(sanitized, "[1, 2]");
    }

    [TestMethod]
    [DataRow("[1, 2,")]
    [DataRow("[{\"id\": 1},")]
    [DataRow("{\"key\": 123,")]
    public void TrySanitize_TruncatedJsonEndingWithComma_TrimsCommaAndBalancesBrackets(string raw)
    {
        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON for {raw}, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_PythonLiterals_NormalizesTrueFalseNone()
    {
        // Arrange - Python unquoted literals True, False, None alongside literal string "True"
        string raw = "{\"valid\": True, \"empty\": None, \"active\": False, \"name\": \"True\"}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"valid\": true");
        StringAssert.Contains(sanitized, "\"empty\": null");
        StringAssert.Contains(sanitized, "\"active\": false");
        StringAssert.Contains(sanitized, "\"name\": \"True\"");
    }

    [TestMethod]
    public void TrySanitize_DoubleQuotedJsonWithEscapedSingleQuote_RepairsInvalidEscape()
    {
        // Arrange - LLM emitted invalid JSON escape \' inside double-quoted string
        string raw = "{\"text\": \"don\\'t fail\", \"status\": \"it\\'s ok\"}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"don't fail\"");
        StringAssert.Contains(sanitized, "\"it's ok\"");
    }

    [TestMethod]
    public void TrySanitize_HeuristicFallback_WithHyphenatedKeyAndSmartQuotes_ExtractsCleanJson()
    {
        // Arrange - Loose key-value with smart quotes and hyphenated property name
        string raw = "\u201Ccontent-type\u201D: \u201Capplication/json\u201D";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"content-type\": \"application/json\"");
    }

    [TestMethod]
    public void TrySanitize_MarkdownCodeFences_StripsFencesAndParsesSuccessfully()
    {
        // Arrange - LLM wrapped JSON array in markdown code fence
        string raw = "```json\n[\n  {\"id\": 1, \"name\": \"first\"},\n  {\"id\": 2, \"name\": \"second\"}\n]\n```";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.StartsWith(sanitized, "[", $"Expected array structure preserved, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"name\": \"first\"");
        StringAssert.Contains(sanitized, "\"name\": \"second\"");
    }

    [TestMethod]
    public void TrySanitize_JsonWithComments_StripsCommentsAndPreservesUrls()
    {
        // Arrange - JSON array with single-line and multi-line comments alongside URLs containing //
        string raw = "[\n  // First endpoint\n  {\"url\": \"https://example.com/api/v1\"},\n  /* Second endpoint */\n  {\"url\": \"https://example.com/api/v2\"}\n]";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.StartsWith(sanitized, "[", $"Expected array structure preserved, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"url\": \"https://example.com/api/v1\"");
        StringAssert.Contains(sanitized, "\"url\": \"https://example.com/api/v2\"");
    }

    [TestMethod]
    public void TrySanitize_TrailingSemicolon_TrimsSemicolonAndParses()
    {
        // Arrange - Code generator appended trailing semicolon to an array
        string raw = "[{\"id\": 1}, {\"id\": 2}];";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.StartsWith(sanitized, "[", $"Expected array structure preserved, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_SingleQuotedContractionsWithoutBackslash_PreservesContractions()
    {
        // Arrange - Single-quoted dict where model did not escape apostrophes in contractions
        string raw = "{'message': 'it's working and don't stop', 'status': 'ok'}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"it's working and don't stop\"");
        StringAssert.Contains(sanitized, "\"status\": \"ok\"");
    }

    [TestMethod]
    public void TrySanitize_SingleKeyWithTrailingBackslash_EscapesTrailingBackslash()
    {
        // Arrange - Windows folder path ending in a backslash before the quote
        string raw = "{\"path\": \"C:\\Users\\test\\\"}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_UnquotedObjectKeys_QuotesKeysAndParses()
    {
        // Arrange - JSON5 / JavaScript object literal with bare unquoted keys
        string raw = "{action: \"run\", params: {id: 123, target: \"prod\"}}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"action\": \"run\"");
        StringAssert.Contains(sanitized, "\"params\": {\"id\": 123, \"target\": \"prod\"}");
    }

    [TestMethod]
    public void TrySanitize_NaNAndInfinity_NormalizesToNull()
    {
        // Arrange - Python/JS float literals NaN, Infinity, -Infinity
        string raw = "{\"loss\": NaN, \"max_val\": Infinity, \"min_val\": -Infinity}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"loss\": null");
        StringAssert.Contains(sanitized, "\"max_val\": null");
        StringAssert.Contains(sanitized, "\"min_val\": null");
    }

    [TestMethod]
    public void TrySanitize_WindowsPathWithNonHexUnicodeEscape_EscapesBackslashAndParses()
    {
        // Arrange - Windows path containing \u not followed by 4 hex digits (e.g. \users, \utils)
        string raw = "{\"folder\": \"C:\\users\\admin\\utils\"}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "C:\\\\users\\\\admin\\\\utils");
    }

    [TestMethod]
    public void TrySanitize_PythonHashComments_StripsCommentsAndPreservesHashInStrings()
    {
        // Arrange - Python/YAML style hash comment outside strings, with hash inside string in an array
        string raw = "[\n  # Primary color\n  {\"color\": \"#FF5733\"},\n  # Secondary color\n  {\"color\": \"#00FF00\"}\n]";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.StartsWith(sanitized, "[", $"Expected array preserved, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"color\": \"#FF5733\"");
        StringAssert.Contains(sanitized, "\"color\": \"#00FF00\"");
    }

    [TestMethod]
    public void TrySanitize_ConversationalPreambleWrappingArray_ExtractsAndPreservesArray()
    {
        // Arrange - Conversational text surrounding a JSON array without markdown code fences
        string raw = "Here is the execution plan for your request:\n[\n  {\"id\": 1, \"action\": \"search\"},\n  {\"id\": 2, \"action\": \"analyze\"}\n]\nHope this helps!";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.StartsWith(sanitized, "[", $"Expected array preserved, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"action\": \"search\"");
        StringAssert.Contains(sanitized, "\"action\": \"analyze\"");
    }

    [TestMethod]
    public void TrySanitize_NumbersWithLeadingPlusSign_StripsPlusAndParses()
    {
        // Arrange - Explicit positive signs on numbers violating RFC 8259
        string raw = "{\"delta\": +10, \"rate\": +0.75, \"temperature\": +25}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"delta\": 10");
        StringAssert.Contains(sanitized, "\"rate\": 0.75");
        StringAssert.Contains(sanitized, "\"temperature\": 25");
    }

    [TestMethod]
    public void TrySanitize_PythonTuples_ConvertsToArraysAndParses()
    {
        // Arrange - Python tuple values emitted for coordinates or multi-values
        string raw = "{\"coords\": (10, 20), \"roles\": (\"admin\", \"editor\")}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"coords\": [10, 20]");
        StringAssert.Contains(sanitized, "\"roles\": [\"admin\", \"editor\"]");
    }

    [TestMethod]
    public void TrySanitize_NestedUnquotedStringValues_QuotesValuesAndParses()
    {
        // Arrange - JSON5 unquoted string identifier values inside nested structures
        string raw = "{\"config\": {\"status\": active, \"env\": production, \"retry\": true}}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"status\": \"active\"");
        StringAssert.Contains(sanitized, "\"env\": \"production\"");
        StringAssert.Contains(sanitized, "\"retry\": true");
    }

    [TestMethod]
    public void TrySanitize_ConsecutiveDoubledCommas_CollapsesCommasAndParses()
    {
        // Arrange - Accidental consecutive commas in arrays and objects
        string raw = "{\"items\": [1,, 2], \"settings\": {\"a\": 10,, \"b\": 20}}";

        // Act
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"items\": [1, 2]");
        StringAssert.Contains(sanitized, "\"a\": 10, \"b\": 20");
    }

    [TestMethod]
    public void TrySanitize_ConversationalPostambleAfterObjectAndArray_StripsPostambleAndPreservesStructure()
    {
        // Arrange - Valid JSON object and array followed by conversational sign-offs
        string rawObj = "{\"action\": \"test\", \"value\": 42}\n\nHope this helps!";
        string rawArr = "[{\"id\": 1}, {\"id\": 2}]\n\nLet me know if you need any adjustments.";

        // Act
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);
        var (sanitizedArr, wasSanitizedArr, _) = JsonSanitizer.TrySanitize(rawArr);

        // Assert
        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"action\": \"test\"");
        StringAssert.Contains(sanitizedObj, "\"value\": 42");
        Assert.IsFalse(sanitizedObj.Contains("Hope this helps"));

        Assert.IsTrue(wasSanitizedArr);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedArr), $"Expected valid JSON, got: {sanitizedArr}");
        Assert.IsTrue(sanitizedArr.StartsWith('[') && sanitizedArr.EndsWith(']'));
        StringAssert.Contains(sanitizedArr, "{\"id\": 1}");
        StringAssert.Contains(sanitizedArr, "{\"id\": 2}");
        Assert.IsFalse(sanitizedArr.Contains("Let me know"));
    }

    [TestMethod]
    public void TrySanitize_UnquotedStringElementsInArray_QuotesElementsAndParses()
    {
        // Arrange - Arrays with unquoted string identifiers (JSON5 style bare words in arrays)
        string raw1 = "[active, pending, suspended]";
        string raw2 = "[\"admin\", editor, guest]";

        // Act
        var (sanitized1, wasSanitized1, _) = JsonSanitizer.TrySanitize(raw1);
        var (sanitized2, wasSanitized2, _) = JsonSanitizer.TrySanitize(raw2);

        // Assert
        Assert.IsTrue(wasSanitized1);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized1), $"Expected valid JSON, got: {sanitized1}");
        Assert.AreEqual("[\"active\", \"pending\", \"suspended\"]", sanitized1);

        Assert.IsTrue(wasSanitized2);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized2), $"Expected valid JSON, got: {sanitized2}");
        Assert.AreEqual("[\"admin\", \"editor\", \"guest\"]", sanitized2);
    }

    [TestMethod]
    public void TrySanitize_SignedNaNLiterals_NormalizesToNull()
    {
        // Arrange - Signed NaN variants (-NaN, +NaN, -nan, +nan) emitted by Python/numpy models
        string raw = "{\"val1\": -NaN, \"val2\": +NaN, \"val3\": -nan, \"val4\": +nan}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        Assert.IsFalse(sanitized.Contains("-null"), "Should not contain illegal -null");
        Assert.IsFalse(sanitized.Contains("+null"), "Should not contain illegal +null");
        StringAssert.Contains(sanitized, "\"val1\": null");
        StringAssert.Contains(sanitized, "\"val2\": null");
        StringAssert.Contains(sanitized, "\"val3\": null");
        StringAssert.Contains(sanitized, "\"val4\": null");
    }

    [TestMethod]
    public void TrySanitize_HexadecimalNumbers_ConvertsToDecimalAndParses()
    {
        // Arrange - Hexadecimal integer parameters outside strings (e.g. masks, flags, offsets)
        string rawObj = "{\"mask\": 0xFF, \"flags\": 0x10, \"offset\": 0x0}";
        string rawArr = "[0x1, 0x2, 0xA]";

        // Act
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);
        var (sanitizedArr, wasSanitizedArr, _) = JsonSanitizer.TrySanitize(rawArr);

        // Assert
        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"mask\": 255");
        StringAssert.Contains(sanitizedObj, "\"flags\": 16");
        StringAssert.Contains(sanitizedObj, "\"offset\": 0");

        Assert.IsTrue(wasSanitizedArr);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedArr), $"Expected valid JSON, got: {sanitizedArr}");
        Assert.AreEqual("[1, 2, 10]", sanitizedArr);
    }

    [TestMethod]
    public void TrySanitize_LeadingAndTrailingFloatDecimalPoints_NormalizesAndParses()
    {
        // Arrange - Floats with leading or trailing dots (.5 -> 0.5, 5. -> 5.0)
        string raw = "{\"ratio\": .5, \"delta\": -.25, \"step\": 1., \"scale\": -5.}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"ratio\": 0.5");
        StringAssert.Contains(sanitized, "\"delta\": -0.25");
        StringAssert.Contains(sanitized, "\"step\": 1.0");
        StringAssert.Contains(sanitized, "\"scale\": -5.0");
    }

    [TestMethod]
    public void TrySanitize_UndefinedLiteral_NormalizesToNull()
    {
        // Arrange - JavaScript undefined token outside strings
        string raw = "{\"result\": undefined, \"active\": true}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"result\": null");
        Assert.IsFalse(sanitized.Contains("\"result\": \"undefined\""), "undefined should not be converted to a string");
        StringAssert.Contains(sanitized, "\"active\": true");
    }

    [TestMethod]
    public void TrySanitize_UnescapedQuotesPrecedingCommasInMultiKeyObject_EscapesAndPreservesSubsequentKeys()
    {
        // Arrange - String value containing unescaped quote immediately preceding a comma ("hello", "world"), followed by another property
        string raw = "{\"action\": \"bash\", \"command\": \"echo \"hello\", \"world\" && ls\", \"timeout\": 30}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"action\": \"bash\"");
        StringAssert.Contains(sanitized, "\"command\": \"echo \\\"hello\\\", \\\"world\\\" && ls\"");
        StringAssert.Contains(sanitized, "\"timeout\": 30");
    }

    [TestMethod]
    public void TrySanitize_PythonTripleQuotedStrings_ConvertsToDoubleQuotesAndParses()
    {
        // Arrange - Python multiline triple-quoted strings ("""...""" and '''...''')
        string raw = "{\"script\": \"\"\"def hello():\n    return \"world\"\"\"\", \"lang\": \"python\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"lang\": \"python\"");
        StringAssert.Contains(sanitized, "\"script\": \"def hello():\\n    return \\\"world\\\"\"");
    }

    [TestMethod]
    public void TrySanitize_UnescapedQuotesInArrayElements_EscapesQuotesAndParses()
    {
        // Arrange - Array element with interior unescaped quote preceding comma
        string raw1 = "[\"echo \"hello\", \"world\"\", \"second element\"]";
        string raw2 = "{\"cmd\": [\"bash\", \"-c\", \"echo \"foo\", \"bar\" && ls\"]}";

        // Act
        var (sanitized1, wasSanitized1, _) = JsonSanitizer.TrySanitize(raw1);
        var (sanitized2, wasSanitized2, _) = JsonSanitizer.TrySanitize(raw2);

        // Assert
        Assert.IsTrue(wasSanitized1);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized1), $"Expected valid JSON, got: {sanitized1}");
        StringAssert.Contains(sanitized1, "\"echo \\\"hello\\\", \\\"world\\\"\"");
        StringAssert.Contains(sanitized1, "\"second element\"");

        Assert.IsTrue(wasSanitized2);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized2), $"Expected valid JSON, got: {sanitized2}");
        StringAssert.Contains(sanitized2, "\"echo \\\"foo\\\", \\\"bar\\\" && ls\"");
    }

    [TestMethod]
    public void TrySanitize_LanguageStringPrefixes_StripsPrefixAndParses()
    {
        // Arrange - Language string prefixes (r"...", @"...", b"...", f"...", $"...") outside strings
        string raw = "{\"pattern\": r\"^\\d+$\", \"path\": @\"C:\\test\", \"data\": b\"hello\", \"msg\": $\"interpolated\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"pattern\": \"^\\\\d+$\"");
        StringAssert.Contains(sanitized, "\"data\": \"hello\"");
        StringAssert.Contains(sanitized, "\"msg\": \"interpolated\"");
    }

    [TestMethod]
    public void TrySanitize_StringConcatenationWithPlus_MergesStringsAndParses()
    {
        // Arrange - Concatenated strings joined with '+' outside strings
        string raw = "{\"query\": \"SELECT * FROM users \" + \"WHERE id = 1\", \"msg\": \"Hello \" + \"World!\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"query\": \"SELECT * FROM users WHERE id = 1\"");
        StringAssert.Contains(sanitized, "\"msg\": \"Hello World!\"");
    }

    [TestMethod]
    public void TrySanitize_BinaryAndOctalNumbers_ConvertsToDecimalAndParses()
    {
        // Arrange - Binary (0b1010 -> 10), Octal (0o755 -> 493), signed binary (-0b11 -> -3), signed hex (+0xFF -> 255)
        string raw = "{\"chmod\": 0o755, \"mask\": 0b1010, \"neg_bin\": -0b11, \"plus_hex\": +0xFF}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"chmod\": 493");
        StringAssert.Contains(sanitized, "\"mask\": 10");
        StringAssert.Contains(sanitized, "\"neg_bin\": -3");
        StringAssert.Contains(sanitized, "\"plus_hex\": 255");
    }

    [TestMethod]
    public void TrySanitize_NumericSeparators_StripsUnderscoresAndParses()
    {
        // Arrange - Underscores inside numbers (1_000_000, 0.000_001, 0xFF_FF, -5_000)
        string raw = "{\"timeout_ms\": 1_000_000, \"rate\": 0.000_001, \"mask\": 0xFF_FF, \"limit\": -5_000}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"timeout_ms\": 1000000");
        StringAssert.Contains(sanitized, "\"rate\": 0.000001");
        StringAssert.Contains(sanitized, "\"mask\": 65535");
        StringAssert.Contains(sanitized, "\"limit\": -5000");
    }

    [TestMethod]
    public void TrySanitize_UppercaseBooleansNullsAndNil_NormalizesToStandardLiterals()
    {
        // Arrange - TRUE, FALSE, NULL, nil outside strings
        string raw = "{\"active\": TRUE, \"deleted\": FALSE, \"payload\": NULL, \"extra\": nil}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"active\": true");
        StringAssert.Contains(sanitized, "\"deleted\": false");
        StringAssert.Contains(sanitized, "\"payload\": null");
        StringAssert.Contains(sanitized, "\"extra\": null");
    }

    [TestMethod]
    public void TrySanitize_LeadingZeroIntegers_NormalizesToValidJsonNumbers()
    {
        // Arrange - Leading zeros in integers per RFC 8259 (007 -> 7, 09 -> 9, 05 -> 5, -012 -> -12, 00 -> 0), preserving 0.5
        string raw = "{\"code\": 007, \"month\": 09, \"day\": 05, \"neg\": -012, \"zero\": 00, \"float\": 0.5}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"code\": 7");
        StringAssert.Contains(sanitized, "\"month\": 9");
        StringAssert.Contains(sanitized, "\"day\": 5");
        StringAssert.Contains(sanitized, "\"neg\": -12");
        StringAssert.Contains(sanitized, "\"zero\": 0");
        StringAssert.Contains(sanitized, "\"float\": 0.5");
    }

    [TestMethod]
    public void TrySanitize_UnquotedFilePaths_QuotesPathsAndParses()
    {
        // Arrange - Unquoted Unix / relative file paths starting with /, ./, or ../
        string raw = "{\"path\": /app/workdir/test.txt, \"script\": ./scripts/run.sh, \"parent\": ../config.json}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"path\": \"/app/workdir/test.txt\"");
        StringAssert.Contains(sanitized, "\"script\": \"./scripts/run.sh\"");
        StringAssert.Contains(sanitized, "\"parent\": \"../config.json\"");
    }

    [TestMethod]
    public void TrySanitize_ConversationalPreambleWithSquareBrackets_ExtractsValidObjectPayload()
    {
        // Arrange - Conversational preamble contains bracketed options [opt1, opt2] before an actual JSON object payload
        string raw = "Here are the selected options [opt1, opt2]: {\"action\": \"execute\", \"target\": \"all\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"action\": \"execute\"");
        StringAssert.Contains(sanitized, "\"target\": \"all\"");
        Assert.IsFalse(sanitized.Contains("opt1"), $"Should not extract bracket text, got: {sanitized}");
    }

    [TestMethod]
    public void TrySanitize_UnquotedUrlsWithProtocol_PreservesAndQuotesUrl()
    {
        // Arrange - Unquoted URLs with http:// and https:// should not have // stripped as single-line comments and should be quoted
        string raw = "{\"api\": https://api.github.com/repos/test, \"local\": http://localhost:8080/v1/health}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"api\": \"https://api.github.com/repos/test\"");
        StringAssert.Contains(sanitized, "\"local\": \"http://localhost:8080/v1/health\"");
    }

    [TestMethod]
    public void TrySanitize_UnquotedWindowsFilePaths_QuotesAndEscapesPath()
    {
        // Arrange - Windows drive-letter paths unquoted (C:\... and D:/...)
        string raw = "{\"input\": C:\\Users\\user1\\Documents\\data.csv, \"output\": D:/backup/export.json}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"input\": \"C:\\\\Users\\\\user1\\\\Documents\\\\data.csv\"");
        StringAssert.Contains(sanitized, "\"output\": \"D:/backup/export.json\"");
    }

    [TestMethod]
    public void TrySanitize_MissingCommasBetweenLines_InsertsCommasAndParses()
    {
        // Arrange - Multi-line JSON objects and arrays where trailing commas were omitted between lines
        string rawObj = "{\n  \"command\": \"build\"\n  \"timeout\": 30\n  \"enabled\": true\n}";
        string rawArr = "[\n  \"first element\"\n  \"second element\"\n  42\n]";

        // Act
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);
        var (sanitizedArr, wasSanitizedArr, _) = JsonSanitizer.TrySanitize(rawArr);

        // Assert
        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"command\": \"build\"");
        StringAssert.Contains(sanitizedObj, "\"timeout\": 30");

        Assert.IsTrue(wasSanitizedArr);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedArr), $"Expected valid JSON, got: {sanitizedArr}");
        StringAssert.Contains(sanitizedArr, "\"first element\"");
        StringAssert.Contains(sanitizedArr, "\"second element\"");
    }

    [TestMethod]
    public void TrySanitize_BacktickTemplateStrings_ConvertsToDoubleQuotesAndParses()
    {
        // Arrange - JS/TS backtick template strings (`...`) used instead of double quotes, containing inner quotes and newlines
        string raw = "{\"query\": `SELECT * FROM \"users\" WHERE id = 1`, \"script\": `def run():\n    print(\"ok\")`}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"query\": \"SELECT * FROM \\\"users\\\" WHERE id = 1\"");
        StringAssert.Contains(sanitized, "\"script\": \"def run():\\n    print(\\\"ok\\\")\"");
    }

    [TestMethod]
    public void TrySanitize_BareKeysWithDotsAndSchemaPrefixes_QuotesKeysAndParses()
    {
        // Arrange - Unquoted keys with dots (user.name) and schema/JSON-LD prefixes (@type, @id)
        string raw = "{user.name: \"Alice\", app.version: 1.0, @type: \"Person\", @id: 42}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"user.name\": \"Alice\"");
        StringAssert.Contains(sanitized, "\"app.version\": 1");
        StringAssert.Contains(sanitized, "\"@type\": \"Person\"");
        StringAssert.Contains(sanitized, "\"@id\": 42");
    }

    [TestMethod]
    public void TrySanitize_ShorthandInfinitiesAndExtendedCasingLiterals_NormalizesToStandardLiterals()
    {
        // Arrange - inf, -inf, +inf and casing variations Null, NONE, Nil, NIL outside strings
        string raw = "{\"loss\": inf, \"min\": -inf, \"pos\": +inf, \"v1\": Null, \"v2\": NONE, \"v3\": Nil, \"v4\": NIL}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"loss\": null");
        StringAssert.Contains(sanitized, "\"min\": null");
        StringAssert.Contains(sanitized, "\"pos\": null");
        StringAssert.Contains(sanitized, "\"v1\": null");
        StringAssert.Contains(sanitized, "\"v2\": null");
        StringAssert.Contains(sanitized, "\"v3\": null");
        StringAssert.Contains(sanitized, "\"v4\": null");
    }

    [TestMethod]
    public void TrySanitize_ScientificNotationWithLeadingDecimalPoint_NormalizesToValidJsonNumber()
    {
        // Arrange - Leading and trailing decimal points combined with scientific exponents (.5e2 -> 0.5e2, -.5e-3 -> -0.5e-3, 5.e3 -> 5.0e3)
        string raw = "{\"rate\": .5e2, \"decay\": -.5e-3, \"scale\": 5.e3}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"rate\": 0.5e2");
        StringAssert.Contains(sanitized, "\"decay\": -0.5e-3");
        StringAssert.Contains(sanitized, "\"scale\": 5.0e3");
    }

    [TestMethod]
    public void TrySanitize_NestedUnclosedContainers_BalancesBracketsProperly()
    {
        // Arrange - Outer object or array closes without closing nested container
        string raw1 = "{\"items\": [\"a\", \"b\"}";
        string raw2 = "[{\"id\": 1, \"name\": \"test\"]";

        // Act
        var (sanitized1, wasSanitized1, _) = JsonSanitizer.TrySanitize(raw1);
        var (sanitized2, wasSanitized2, _) = JsonSanitizer.TrySanitize(raw2);

        // Assert
        Assert.IsTrue(wasSanitized1);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized1), $"Expected valid JSON, got: {sanitized1}");
        StringAssert.Contains(sanitized1, "\"items\": [\"a\", \"b\"]");

        Assert.IsTrue(wasSanitized2);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized2), $"Expected valid JSON, got: {sanitized2}");
        StringAssert.Contains(sanitized2, "{\"id\": 1, \"name\": \"test\"}");
    }

    [TestMethod]
    public void TrySanitize_SingleLineMissingCommas_InsertsCommasAndParses()
    {
        // Arrange - Space-delimited properties and array elements on a single line without commas
        string rawObj = "{\"cmd\": \"build\" \"timeout\": 30 \"enabled\": true}";
        string rawArr = "[\"first\" \"second\" \"third\"]";

        // Act
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);
        var (sanitizedArr, wasSanitizedArr, _) = JsonSanitizer.TrySanitize(rawArr);

        // Assert
        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"cmd\": \"build\", \"timeout\": 30, \"enabled\": true");

        Assert.IsTrue(wasSanitizedArr);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedArr), $"Expected valid JSON, got: {sanitizedArr}");
        StringAssert.Contains(sanitizedArr, "[\"first\", \"second\", \"third\"]");
    }

    [TestMethod]
    public void TrySanitize_UnquotedKeysAndValuesOnNewlinesWithoutCommas_QuotesAndParses()
    {
        // Arrange - Bare keys and unquoted URLs/paths on separate lines without trailing commas
        string raw = "{\n  api: https://api.example.com/v1\n  dest: C:\\Data\\output.csv\n  status: active\n}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"api\": \"https://api.example.com/v1\"");
        StringAssert.Contains(sanitized, "\"dest\": \"C:\\\\Data\\\\output.csv\"");
        StringAssert.Contains(sanitized, "\"status\": \"active\"");
    }

    [TestMethod]
    public void TrySanitize_KeyAssignmentEqualsSign_NormalizesToColonAndParses()
    {
        // Arrange - Python/HCL assignment equals instead of colons for properties
        string raw = "{\"command\" = \"run\", \"timeout\" = 30, action = \"build\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"command\": \"run\"");
        StringAssert.Contains(sanitized, "\"timeout\": 30");
        StringAssert.Contains(sanitized, "\"action\": \"build\"");
    }

    [TestMethod]
    public void TrySanitize_PythonSetSyntax_ConvertsToJsonArrayAndParses()
    {
        // Arrange - Python sets ({'a', 'b'} or {"a", "b"}) containing comma-separated elements without keys
        string rawObj = "{\"tags\": {\"fast\", \"accurate\", \"secure\"}}";
        string rawSet = "{\"item1\", \"item2\", \"item3\"}";

        // Act
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);
        var (sanitizedSet, wasSanitizedSet, _) = JsonSanitizer.TrySanitize(rawSet);

        // Assert
        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"tags\": [\"fast\", \"accurate\", \"secure\"]");

        Assert.IsTrue(wasSanitizedSet);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedSet), $"Expected valid JSON, got: {sanitizedSet}");
        StringAssert.Contains(sanitizedSet, "[\"item1\", \"item2\", \"item3\"]");
    }

    [TestMethod]
    public void TrySanitize_InBodySemicolons_ConvertsToCommasAndParses()
    {
        // Arrange - Properties delimited by semicolons inside the object body
        string raw = "{\"cmd\": \"compile\"; \"flags\": \"-O2\"; \"debug\": false;}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"cmd\": \"compile\"");
        StringAssert.Contains(sanitized, "\"flags\": \"-O2\"");
        StringAssert.Contains(sanitized, "\"debug\": false");
    }

    [TestMethod]
    public void TrySanitize_EllipsisAndTruncationMarkers_StripsAndParses()
    {
        // Arrange - Array and object truncation ellipsis placeholders (... and \u2026)
        string rawArr = "[\"first\", \"second\", ...]";
        string rawObj = "{\"id\": 1, \"name\": \"sample\", …}";

        // Act
        var (sanitizedArr, wasSanitizedArr, _) = JsonSanitizer.TrySanitize(rawArr);
        var (sanitizedObj, wasSanitizedObj, _) = JsonSanitizer.TrySanitize(rawObj);

        // Assert
        Assert.IsTrue(wasSanitizedArr);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedArr), $"Expected valid JSON, got: {sanitizedArr}");
        StringAssert.Contains(sanitizedArr, "[\"first\", \"second\"]");

        Assert.IsTrue(wasSanitizedObj);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitizedObj), $"Expected valid JSON, got: {sanitizedObj}");
        StringAssert.Contains(sanitizedObj, "\"id\": 1");
        StringAssert.Contains(sanitizedObj, "\"name\": \"sample\"");
    }

    [TestMethod]
    public void TrySanitize_SignedLeadingDotFloatsAndHexEscapes_NormalizesAndParses()
    {
        // Arrange - Explicit positive sign on leading-dot float (+.5 -> 0.5) and 2-digit hex escape (\x20 -> \u0020)
        string raw = "{\"delta\": +.5, \"ratio\": +.5e2, \"msg\": \"Hello\\x20World\\x21\"}";

        // Act
        var (sanitized, wasSanitized, _) = JsonSanitizer.TrySanitize(raw);

        // Assert
        Assert.IsTrue(wasSanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized), $"Expected valid JSON, got: {sanitized}");
        StringAssert.Contains(sanitized, "\"delta\": 0.5");
        StringAssert.Contains(sanitized, "\"ratio\": 0.5e2");
        StringAssert.Contains(sanitized, "\"msg\": \"Hello\\u0020World\\u0021\"");
    }

    [TestMethod]
    public void TrySanitize_PythonScriptWithFStringAndQuotes_PreservesValidJson()
    {
        string raw = "{\"script\": \"table.append(f'| Temperature (°C) | {row[\\\"min\\\"]:.1f} |')\"}";
        var (sanitized, wasSanitized, warning) = JsonSanitizer.TrySanitize(raw);
        Assert.IsFalse(wasSanitized, $"Was sanitized unexpectedly with warning: {warning}\nSanitized output:\n{sanitized}");
        Assert.AreEqual(raw, sanitized);
        Assert.IsTrue(JsonSanitizer.IsValidJson(sanitized));
    }
}
