using Parsley.Runtime;
namespace Sql92Test
{
    internal class Program
    {
        // SQL-92 statements for the demo, from simple to involved. The last one is malformed on purpose, to show
        // the error reporting.
        static readonly string[] Statements =
        {
            "select * FROM employees",

            "SELECT e.name, e.salary AS pay\n" +
            "FROM employees e\n" +
            "WHERE e.salary > 50000 AND e.dept_id IS NOT NULL\n" +
            "ORDER BY pay DESC, 1",

            "SELECT d.name, COUNT(*) AS headcount, AVG(e.salary)\n" +
            "FROM departments d\n" +
            "  LEFT OUTER JOIN employees e ON e.dept_id = d.id\n" +
            "GROUP BY d.name\n" +
            "HAVING COUNT(*) > 5",

            "SELECT name FROM employees\n" +
            "WHERE dept_id IN (SELECT id FROM departments WHERE region = 'West')\n" +
            "  AND hired BETWEEN DATE '2020-01-01' AND CURRENT_DATE",

            "SELECT id FROM customers\n" +
            "UNION\n" +
            "SELECT id FROM suppliers\n" +
            "EXCEPT\n" +
            "SELECT id FROM blocked",

            "SELECT name,\n" +
            "  CASE WHEN salary >= 100000 THEN 'high'\n" +
            "       WHEN salary >= 50000 THEN 'medium'\n" +
            "       ELSE 'low' END AS band,\n" +
            "  COALESCE(nickname, name) AS display_name\n" +
            "FROM employees",

            "SELECT UPPER(TRIM(LEADING ' ' FROM name)) || ' (' || CAST(id AS VARCHAR(10)) || ')',\n" +
            "  SUBSTRING(code FROM 1 FOR 3), EXTRACT(YEAR FROM hired)\n" +
            "FROM employees",

            "SELECT (end_time - start_time) DAY TO SECOND AS duration\n" +
            "FROM shifts\n" +
            "WHERE EXISTS (SELECT * FROM breaks b WHERE b.shift_id = shifts.id)",

            "VALUES (1, 'one'), (2, 'two'), (3, 'three')",

            "SELECT name FROM employees WHERE salary > :min_salary AND dept_id = ?",

            // malformed: no select list
            "SELECT FROM employees WHERE"
        };

        static void Main(string[] args)
        {
            for (var i = 0; i < Statements.Length; ++i)
            {
                var sql = Statements[i];
                Console.WriteLine($"=== Statement {i + 1} of {Statements.Length} ===");
                Console.WriteLine();
                Console.WriteLine(sql);
                Console.WriteLine();
                try
                {
                    // parse the tree
                    var tree = Sql92Parser.Parse(sql);
                    Console.WriteLine(tree);
                }
                catch (ParseException e)
                {
                    foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
                }
                Console.WriteLine();
                _Pause(i < Statements.Length - 1 ? "Press any key for the next statement..." : "Press any key to exit...");
            }
        }

        static void _Pause(string prompt)
        {
            Console.Write(prompt);
            // ReadKey throws when input is redirected, as when run from a script
            if (Console.IsInputRedirected) Console.ReadLine();
            else Console.ReadKey(true);
            Console.WriteLine();
            Console.WriteLine();
        }
    }
}