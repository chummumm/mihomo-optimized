// Test-only output contract for the one-shot Windows PowerShell UIA client.
// Do not reflect arbitrary CLR/COM objects or PowerShell extended properties.
using System;
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text;

public sealed class UiaResultJson
{
    private const int MaximumDepth = 16;
    private const int MaximumNodes = 1000000;
    private const int MaximumCharacters = 16 * 1024 * 1024;
    private readonly StringBuilder output = new StringBuilder();
    private int nodes;

    public static string Encode(object value)
    {
        UiaResultJson writer = new UiaResultJson();
        writer.Value(value, 0);
        return writer.output.ToString();
    }

    private void Append(string value)
    {
        if (value.Length > MaximumCharacters - output.Length)
            throw new InvalidOperationException("uia_json_output_limit");
        output.Append(value);
    }

    private void Append(string value, int start, int count)
    {
        if (count > MaximumCharacters - output.Length)
            throw new InvalidOperationException("uia_json_output_limit");
        output.Append(value, start, count);
    }

    private void String(string value)
    {
        if (value.Length > MaximumCharacters - output.Length - 2)
            throw new InvalidOperationException("uia_json_output_limit");
        Append("\"");
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character >= 0x20 && character <= 0x7e && character != '"' && character != '\\')
                continue;
            Append(value, start, index - start);
            if (character == '"') Append("\\\"");
            else if (character == '\\') Append("\\\\");
            // Escape every non-ASCII UTF-16 unit, including unpaired surrogates.
            // Encoding stdout as UTF-8 must never replace observed UI text.
            else
                Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
            start = index + 1;
        }
        Append(value, start, value.Length - start);
        Append("\"");
    }

    private void Value(object value, int depth)
    {
        if (depth > MaximumDepth || ++nodes > MaximumNodes)
            throw new InvalidOperationException("uia_json_structure_limit");
        int wrappers = 0;
        while (value is PSObject)
        {
            if (++wrappers > MaximumDepth)
                throw new InvalidOperationException("uia_json_wrapper_limit");
            value = ((PSObject)value).BaseObject;
        }
        if (value == null) { Append("null"); return; }
        if (value is string) { String((string)value); return; }
        if (value is bool) { Append((bool)value ? "true" : "false"); return; }
        if (value is double)
        {
            double number = (double)value;
            if (double.IsNaN(number) || double.IsInfinity(number))
                throw new InvalidOperationException("uia_json_nonfinite_number");
            Append(number.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (value is float)
        {
            float number = (float)value;
            if (float.IsNaN(number) || float.IsInfinity(number))
                throw new InvalidOperationException("uia_json_nonfinite_number");
            Append(number.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong || value is decimal)
        {
            Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
            return;
        }
        IDictionary dictionary = value as IDictionary;
        if (dictionary != null)
        {
            Append("{");
            bool first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!(entry.Key is string))
                    throw new InvalidOperationException("uia_json_nonstring_key");
                if (!first) Append(",");
                first = false;
                String((string)entry.Key);
                Append(":");
                Value(entry.Value, depth + 1);
            }
            Append("}");
            return;
        }
        IList list = value as IList;
        if (list != null)
        {
            Append("[");
            for (int index = 0; index < list.Count; index++)
            {
                if (index != 0) Append(",");
                Value(list[index], depth + 1);
            }
            Append("]");
            return;
        }
        // An unexpected UIA peer or PowerShell object must fail, not silently
        // omit its properties or traverse a remote/reflection object graph.
        throw new InvalidOperationException("uia_json_unsupported_type");
    }
}
