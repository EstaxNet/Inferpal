using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Terraform plan reads first as what it will do (pari 156): destroyed and replaced first, each resource with what changes;
/// a sensitive value never shown, though the plan's JSON holds it in clear; a state file and any other JSON read as their lines.
/// </summary>
public class TerraformPlanTextTests
{
    // The shape `terraform show -json plan` writes (format 1.2), with its secrets in clear as the real file holds them.
    private const string Plan = """
        {
          "format_version": "1.2", "terraform_version": "1.9.8",
          "planned_values": { "root_module": { "resources": [{ "values": { "password": "PLANNED-SECRET" } }] } },
          "prior_state": { "values": { "root_module": { "resources": [{ "values": { "password": "PRIOR-SECRET" } }] } } },
          "resource_drift": [{ "address": "aws_s3_bucket.logs", "change": { "actions": ["update"] } }],
          "resource_changes": [
            { "address": "aws_instance.web", "change": { "actions": ["update"],
                "before": { "instance_type": "t3.small", "tags": { "env": "dev", "owner": "ops" } },
                "after":  { "instance_type": "t3.large", "tags": { "env": "prod", "owner": "ops" } },
                "after_unknown": {}, "before_sensitive": {}, "after_sensitive": {} } },
            { "address": "aws_db_instance.main", "action_reason": "replace_because_cannot_update",
              "change": { "actions": ["delete", "create"],
                "before": { "engine_version": "14", "password": "OLD-SECRET", "endpoint": "db.old:5432" },
                "after":  { "engine_version": "16", "password": "NEW-SECRET" },
                "after_unknown": { "endpoint": true },
                "before_sensitive": { "password": true }, "after_sensitive": { "password": true },
                "replace_paths": [["engine_version"]] } },
            { "address": "random_password.api", "change": { "actions": ["create"],
                "before": null, "after": { "length": 32, "special": true },
                "after_unknown": { "result": true, "id": true }, "after_sensitive": { "result": true } } },
            { "address": "aws_instance.legacy", "action_reason": "delete_because_no_resource_config",
              "change": { "actions": ["delete"], "before": { "ami": "ami-1" }, "after": null } },
            { "address": "aws_vpc.main", "change": { "actions": ["no-op"], "before": {}, "after": {} } },
            { "address": "data.aws_ami.ubuntu", "change": { "actions": ["read"] } }
          ],
          "output_changes": {
            "db_password": { "actions": ["update"], "before": "OLD-SECRET", "after": "NEW-SECRET",
                             "before_sensitive": true, "after_sensitive": true },
            "endpoint": { "actions": ["update"], "after_unknown": true },
            "region": { "actions": ["no-op"], "before": "eu-west-3", "after": "eu-west-3" }
          }
        }
        """;

    [Fact]
    public void APlan_ReadsDestroyedAndReplacedFirst_EachWithWhatChanges()
    {
        var text = TerraformPlanText.Summarize("plan.json", Plan, 8000)!;
        Assert.StartsWith("Terraform plan (Terraform 1.9.8): 1 to destroy, 1 to replace, 1 to create, 1 to change, 1 data sources to read; 1 unchanged.", text);
        Assert.True(text.IndexOf("Destroy:", StringComparison.Ordinal) < text.IndexOf("Replace", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Replace", StringComparison.Ordinal) < text.IndexOf("Create:", StringComparison.Ordinal));
        Assert.Contains("aws_instance.legacy  (delete_because_no_resource_config)", text);
        Assert.Contains("engine_version: \"14\" → \"16\"  (forces replacement)", text);
        Assert.Contains("endpoint: \"db.old:5432\" → (known after apply)", text);
        Assert.Contains("instance_type: \"t3.small\" → \"t3.large\"", text);
        Assert.Contains("tags.env: \"dev\" → \"prod\"", text);
        Assert.DoesNotContain("tags.owner", text);                      // unchanged leaves are not listed
        Assert.Contains("result = (sensitive)", text);
        Assert.Contains("id = (known after apply)", text);
        Assert.Contains("endpoint: (known after apply)", text);          // an output
        Assert.DoesNotContain("region", text);
        Assert.Contains("1 resource changed outside Terraform since the last apply: aws_s3_bucket.logs", text);
    }

    [Fact]
    public void ASensitiveValue_IsNeverShown_ThoughThePlanHoldsItInClear()
    {
        var text = TerraformPlanText.Summarize("plan.json", Plan, 8000)!;
        Assert.Contains("password: (sensitive) → (sensitive)", text);
        Assert.Contains("db_password: (sensitive)", text);
        Assert.DoesNotContain("SECRET", text);                           // resources, outputs, prior state, planned values
    }

    [Fact]
    public void APlanPastItsBudget_CountsWhatItLeavesOut_AndSaysTheRawJsonHoldsSecrets()
    {
        var text = TerraformPlanText.Summarize("plan.json", Plan, 600)!;
        Assert.Contains("more change", text);
        Assert.Contains("sensitive values in clear", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void AnErroredPlan_IsSaidIncomplete() =>
        Assert.Contains("The plan errored",
            TerraformPlanText.Summarize("plan.json", """{ "format_version": "1.2", "errored": true, "planned_values": {} }""", 8000));

    [Fact]
    public void AStateFile_AndAnyOtherJson_AreReadAsTheirLines()
    {
        Assert.Null(TerraformPlanText.Summarize("state.json", """{ "format_version": "1.0", "values": { "root_module": {} } }""", 8000));
        Assert.Null(TerraformPlanText.Summarize("notes.json", """{ "resource_changes": "see the wiki" }""", 8000));
        Assert.Null(TerraformPlanText.Summarize("plan.txt", Plan, 8000));
    }

    [Fact]
    public void AValueThatBecomesSensitive_HidesItsOldValueToo()
    {
        // As Terraform's own renderer: either side's mark hides both, the old value comes from the state in clear.
        const string plan = """
            { "format_version": "1.2", "resource_changes": [
              { "address": "a.b", "change": { "actions": ["update"],
                "before": { "password": "OLD-SECRET", "token": "TOKEN-SECRET" }, "after": { "password": "NEW-SECRET" },
                "after_unknown": { "token": true }, "before_sensitive": {}, "after_sensitive": { "password": true, "token": true } } }],
              "output_changes": { "o": { "actions": ["update"], "before": "x", "after": "OUT-SECRET", "before_sensitive": true, "after_sensitive": false } } }
            """;
        var text = TerraformPlanText.Summarize("plan.json", plan, 8000)!;
        Assert.Contains("password: (sensitive) → (sensitive)", text);
        Assert.Contains("token: (sensitive) → (sensitive)", text);
        Assert.Contains("o: (sensitive)", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void ForgetImportMoveAndDeferred_AreSaid_NeverCountedUnchanged()
    {
        const string plan = """
            { "format_version": "1.2", "complete": false, "resource_changes": [
              { "address": "aws_s3_bucket.keep", "change": { "actions": ["forget"] } },
              { "address": "aws_instance.imp", "change": { "actions": ["no-op"], "importing": { "id": "i-123" } } },
              { "address": "aws_instance.new", "previous_address": "aws_instance.old", "change": { "actions": ["no-op"] } }],
              "deferred_changes": [{ "reason": "instance_count_unknown", "resource_change": { "address": "aws_x.a", "change": { "actions": ["create"] } } }] }
            """;
        var text = TerraformPlanText.Summarize("plan.json", plan, 8000)!;
        Assert.Contains("1 to forget", text);
        Assert.Contains("1 to import", text);
        Assert.Contains("1 to move", text);
        Assert.DoesNotContain("unchanged", text);
        Assert.Contains("NOT destroyed", text);
        Assert.Contains("aws_instance.imp  (import i-123)", text);
        Assert.Contains("aws_instance.new  (moved from aws_instance.old)", text);
        Assert.Contains("The plan is incomplete: 1 change deferred", text);
        Assert.Contains("aws_x.a  (instance_count_unknown)", text);
    }

    [Fact]
    public void AReplacePathAboveTheLinePrinted_StillMarksIt()
    {
        const string plan = """
            { "format_version": "1.2", "resource_changes": [
              { "address": "a.b", "change": { "actions": ["delete", "create"],
                "before": { "network_interface": [{ "subnet": "a" }] }, "after": { "network_interface": [{ "subnet": "b" }] },
                "replace_paths": [["network_interface"]] } }] }
            """;
        Assert.Contains("""network_interface.0.subnet: "a" → "b"  (forces replacement)""",TerraformPlanText.Summarize("plan.json", plan, 8000));
    }

    [Fact]
    public void APlanWithNoChange_WhichTerraformWritesWithoutResourceChanges_Reads() =>
        Assert.Contains("No changes.", TerraformPlanText.Summarize("plan.json",
            """{ "format_version": "1.2", "planned_values": { "root_module": {} }, "variables": { "pw": { "value": "SECRET" } } }""", 8000));
}
