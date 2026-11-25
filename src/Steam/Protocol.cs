using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dispatch.Model;

namespace Dispatch.Steam
{
    /// <summary>
    /// Line protocol between the window and the lookup process. Requests go in on stdin, one per line;
    /// answers come back on stdout as tab-separated records.
    /// </summary>
    internal static class Protocol
    {
        public const string ServerRecord = "L";
        public const string DoneRecord = "Q";
        public const string FailedRecord = "F";
        public const string ErrorRecord = "E";

        private const char FieldSep = '\t';
        private const char PartSep = '\u001f';

        public static string EncodeQuery(LobbyQuery q) =>
            string.Join(FieldSep.ToString(), q.Filters.Select(f =>
                ((int)f.Comparison).ToString(CultureInfo.InvariantCulture) + PartSep + Clean(f.Key) + PartSep + Clean(f.Value)));

        public static LobbyQuery DecodeQuery(string line)
        {
            var filters = new List<LobbyFilter>();
            foreach (var field in line.Split(FieldSep))
            {
                var parts = field.Split(PartSep);
                if (parts.Length == 3 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cmp))
                {
                    filters.Add(new LobbyFilter(parts[1], parts[2], (Comparison)cmp));
                }
            }

            return new LobbyQuery(filters);
        }

        public static string EncodeServer(int query, Server s) =>
            Join(ServerRecord, query.ToString(CultureInfo.InvariantCulture), s.Id, s.Owner, s.Name, s.Mode, s.Map, s.Bots,
                s.Region, s.Limit.ToString(CultureInfo.InvariantCulture));

        public static Server DecodeServer(string[] f, int rank, DateTime seenUtc)
        {
            if (f.Length < 10)
            {
                return null;
            }

            int.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit);
            return new Server
            {
                Id = f[2],
                Owner = f[3],
                Name = f[4],
                Mode = f[5],
                Map = f[6],
                Bots = f[7],
                Region = f[8],
                Limit = limit,
                Rank = rank,
                SeenUtc = seenUtc,
            };
        }

        public static string EncodeDone(int query, int count) =>
            Join(DoneRecord, query.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture));

        public static string EncodeFailed(int query) => Join(FailedRecord, query.ToString(CultureInfo.InvariantCulture));

        public static string EncodeError(string code, string message) => Join(ErrorRecord, code, message);

        public static string[] Split(string line) => line.Split(FieldSep);

        /// <summary>Removes control characters so a field can never break the record format.</summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                sb.Append(char.IsControl(ch) ? ' ' : ch);
            }

            return sb.ToString();
        }

        private static string Join(params string[] fields) => string.Join(FieldSep.ToString(), fields.Select(Clean));
    }
}
