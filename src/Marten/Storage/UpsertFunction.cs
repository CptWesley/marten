using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Baseline;
using Marten.Schema;
using Marten.Schema.Arguments;
using Marten.Util;

namespace Marten.Storage
{
    public class UpsertFunction : Function
    {
        private readonly string _primaryKeyConstraintName;
        private readonly DbObjectName _tableName;

        public readonly IList<UpsertArgument> Arguments = new List<UpsertArgument>();

        public UpsertFunction(DocumentMapping mapping) : base(mapping.UpsertFunction)
        {
            if (mapping == null) throw new ArgumentNullException(nameof(mapping));

            _tableName = mapping.Table;
            _primaryKeyConstraintName = "pk_" + mapping.Table.Name;
                
            var idType = mapping.IdMember.GetMemberType();
            var pgIdType = TypeMappings.GetPgType(idType);

            Arguments.Add(new UpsertArgument
            {
                Arg = "docId",
                PostgresType = pgIdType,
                Column = "id",
                Members = new[] {mapping.IdMember}
            });

            Arguments.Add(new DocJsonBodyArgument());

            Arguments.AddRange(mapping.DuplicatedFields.Select(x => x.UpsertArgument));

            Arguments.Add(new VersionArgument());

            Arguments.Add(new DotNetTypeArgument());

            if (mapping.IsHierarchy())
            {
                Arguments.Add(new DocTypeArgument());
            }

            if (mapping.UseOptimisticConcurrency)
            {
                Arguments.Add(new CurrentVersionArgument());
            }

            if (mapping.TenancyStyle == TenancyStyle.Conjoined)
            {
                Arguments.Add(new TenantIdArgument());
            }

        }

        public override void Write(DdlRules rules, StringWriter writer)
        {
            var ordered = OrderedArguments();

            var argList = ordered.Select(x => x.ArgumentDeclaration()).Join(", ");

            var systemUpdates = new string[] {$"{DocumentMapping.LastModifiedColumn} = transaction_timestamp()" };
            var updates = ordered.Where(x => x.Column != "id" && x.Column.IsNotEmpty())
                .Select(x => $"\"{x.Column}\" = {x.Arg}").Concat(systemUpdates).Join(", ");

            var inserts = ordered.Where(x => x.Column.IsNotEmpty()).Select(x => $"\"{x.Column}\"").Concat(new [] {DocumentMapping.LastModifiedColumn}).Join(", ");
            var valueList = ordered.Where(x => x.Column.IsNotEmpty()).Select(x => x.Arg).Concat(new [] { "transaction_timestamp()" }).Join(", ");

            if (Arguments.Any(x => x is CurrentVersionArgument))
            {
                updates += $" where {_tableName.QualifiedName}.{DocumentMapping.VersionColumn} = current_version";
            }

            var securityDeclaration = rules.UpsertRights == SecurityRights.Invoker
                ? "SECURITY INVOKER"
                : "SECURITY DEFINER";

            

            writer.WriteLine($@"
CREATE OR REPLACE FUNCTION {Identifier.QualifiedName}({argList}) RETURNS UUID LANGUAGE plpgsql {securityDeclaration} AS $function$
DECLARE
  final_version uuid;
BEGIN
INSERT INTO {_tableName.QualifiedName} ({inserts}) VALUES ({valueList})
  ON CONFLICT ON CONSTRAINT {_primaryKeyConstraintName}
  DO UPDATE SET {updates};

  SELECT mt_version FROM {_tableName.QualifiedName} into final_version WHERE id = docId;
  RETURN final_version;
END;
$function$;
");


        }

        public UpsertArgument[] OrderedArguments()
        {
            return Arguments.OrderBy(x => x.Arg).ToArray();
        }


        protected override string toDropSql()
        {
            var argList = OrderedArguments().Select(x => x.PostgresType).Join(", ");
            var dropSql = $"drop function if exists {Identifier.QualifiedName}({argList});";
            return dropSql;
        }

        public void WriteTemplate(DdlRules rules, DdlTemplate template, StringWriter writer)
        {
            var body = ToBody(rules);
            body.WriteTemplate(template, writer);
        }
    }
}