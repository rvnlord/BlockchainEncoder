using System;
using System.Collections.Generic;
using CommonLib.Source.Common.Converters;

namespace WpfMyCompression.Source.DbContext.Models
{
    public class KV
    {
        public string Key { get; set; }
        public string Value { get; set; }

        public override string ToString() => $"{Key.Base64ToUTF8()}={Value.Base64ToUTF8()}";
    }

    public static class KVConverter
    {
        public static KV ToKV(this IEnumerable<string> en)
        {
            var (k, v) = en.ToTupleOf2();
            return new KV { Key = k, Value = v };
        }
    }
}
