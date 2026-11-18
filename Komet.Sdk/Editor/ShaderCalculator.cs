using System.Globalization;

namespace Komet.Sdk;

// The preprocessor's integer arithmetic: shunting-yard to postfix, then one stack pass (no recursion). Null on any
// malformed input or division by zero.
internal static class Calculator
{
    private const int MaxStack = 128;

    public static long? Run(List<string> tokens) => Postfix(tokens) is { } postfix ? Reduce(postfix) : null;

    private static int Precedence(string op) =>
        op switch
        {
            "!" or "~" or "neg" => 7,
            "*" or "/" or "%" => 6,
            "+" or "-" => 5,
            "<" or ">" or "<=" or ">=" => 4,
            "==" or "!=" => 3,
            "&&" => 2,
            "||" => 1,
            _ => 0
        };

    private static bool Unary(string op) => op is "!" or "~" or "neg";

    private static List<string>? Postfix(List<string> tokens)
    {
        var output = new List<string>();
        var ops = new Stack<string>();
        var expectOperand = true;
        foreach (var raw in tokens.Bounded(ShaderPreprocessor.MaxTokens))
        {
            var token = raw == "-" && expectOperand ? "neg" : raw;
            if (char.IsDigit(token[0]))
            {
                output.Add(token);
                expectOperand = false;
            }
            else if (token == "(")
            {
                ops.Push(token);
                expectOperand = true;
            }
            else if (token == ")")
            {
                if (!Close(ops, output)) return null;

                expectOperand = false;
            }
            else
            {
                Push(ops, output, token);
                expectOperand = true;
            }
        }

        return Drain(ops, output) ? output : null;
    }

    private static bool Close(Stack<string> ops, List<string> output)
    {
        for (var i = 0; i < MaxStack && ops.Count > 0; i++)
        {
            var top = ops.Pop();
            if (top == "(") return true;

            output.Add(top);
        }

        return false;
    }

    private static void Push(Stack<string> ops, List<string> output, string token)
    {
        for (var i = 0; i < MaxStack && ops.Count > 0 && ops.Peek() != "(" && !Unary(token); i++)
        {
            if (Precedence(ops.Peek()) < Precedence(token)) break;

            output.Add(ops.Pop());
        }

        ops.Push(token);
    }

    private static bool Drain(Stack<string> ops, List<string> output)
    {
        for (var i = 0; i < MaxStack && ops.Count > 0; i++)
        {
            var top = ops.Pop();
            if (top == "(") return false;

            output.Add(top);
        }

        return ops.Count == 0;
    }

    private static long? Reduce(List<string> postfix)
    {
        var values = new Stack<long>();
        foreach (var token in postfix.Bounded(ShaderPreprocessor.MaxTokens))
        {
            if (char.IsDigit(token[0]))
            {
                values.Push(long.Parse(token, CultureInfo.InvariantCulture));
                continue;
            }

            var need = Unary(token) ? 1 : 2;
            if (values.Count < need) return null;

            var b = values.Pop();
            var a = need == 2 ? values.Pop() : 0;
            if (Apply(token, a, b) is not { } result) return null;

            values.Push(result);
        }

        return values.Count == 1 ? values.Pop() : null;
    }

    private static long? Apply(string op, long a, long b) =>
        op switch
        {
            "!" => b == 0 ? 1 : 0,
            "~" => ~b,
            "neg" => -b,
            "*" => a * b,
            "/" => b == 0 ? null : a / b,
            "%" => b == 0 ? null : a % b,
            "+" => a + b,
            "-" => a - b,
            "<" => a < b ? 1 : 0,
            ">" => a > b ? 1 : 0,
            "<=" => a <= b ? 1 : 0,
            ">=" => a >= b ? 1 : 0,
            "==" => a == b ? 1 : 0,
            "!=" => a != b ? 1 : 0,
            "&&" => a != 0 && b != 0 ? 1 : 0,
            "||" => a != 0 || b != 0 ? 1 : 0,
            _ => null
        };
}
