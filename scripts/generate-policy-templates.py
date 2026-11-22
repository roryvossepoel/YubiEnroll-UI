"""Generate the English Administrative Templates from the same five profile form definitions."""
from pathlib import Path
import xml.etree.ElementTree as ET

OUT = Path(__file__).resolve().parents[1] / "policy"
NS = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions"
KEY = r"Software\Policies\PasskeyDesk"
sub = ET.SubElement
def root(name):
    return ET.Element(name, revision="1.2", schemaVersion="1.0", xmlns=NS)

admx = root("policyDefinitions")
sub(sub(admx, "policyNamespaces"), "target", prefix="pdesk", namespace="PasskeyDesk.Policies")
sub(admx, "resources", minRequiredRevision="1.2")
support = sub(sub(admx, "supportedOn"), "definitions")
cats = sub(admx, "categories")
policies = sub(admx, "policies")
adml = root("policyDefinitionResources")
sub(adml, "displayName").text = "Passkey Desk for YubiKeys"
sub(adml, "description").text = "Machine policy for the independent Passkey Desk application."
resources = sub(adml, "resources")
strings = sub(resources, "stringTable")
presentations = sub(resources, "presentationTable")
def string(id_, value):
    sub(strings, "string", id=id_).text = value
    return "$(string." + id_ + ")"
sub(support, "definition", name="PolicyPreview", displayName=string("SupportedVersion", "Passkey Desk 0.11.0 Policy Preview 2 or later"))
sub(cats, "category", name="PasskeyDesk", displayName=string("CategoryName", "Passkey Desk for YubiKeys"))
for name, label, parent in [
    ("CliConfig", "YubiEnroll CLI configuration (TOML)", "PasskeyDesk"),
    ("TenantsCategory", "Tenants", "CliConfig"),
    ("Profiles", "Configuration profiles", "CliConfig"),
    ("AppSettings", "Passkey Desk app settings", "PasskeyDesk"),
    ("Logging", "Logging", "AppSettings"),
]:
    category = sub(cats, "category", name=name, displayName=string(name + "LabelCategory", label))
    sub(category, "parentCategory", ref=parent)
for i in ("Off", "On", "Internal", "External"):
    string(i, {"Internal":"In-app terminal", "External":"External console"}.get(i, i))

def policy(name, label, explain, fields, category):
    p = sub(policies, "policy", name=name, **{"class": "Machine"}, key=KEY,
            displayName=string(name + "Label", label), explainText=string(name + "Help", explain),
            presentation="$(presentation." + name + ")")
    sub(p, "parentCategory", ref=category)
    sub(p, "supportedOn", ref="PolicyPreview")
    els = sub(p, "elements")
    form = sub(presentations, "presentation", id=name)
    for field, caption, kind, opts in fields:
        attrs = dict(id=field, valueName=field)
        attrs["required"] = "false" if kind == "folder" else "true"
        if kind in ("text", "folder"):
            sub(els, "text", **attrs, maxLength=str(opts))
            box = sub(form, "textBox", refId=field)
            sub(box, "label").text = caption
        elif kind == "number":
            lo, hi, default = opts
            sub(els, "decimal", **attrs, minValue=str(lo), maxValue=str(hi))
            sub(form, "decimalTextBox", refId=field, defaultValue=str(default)).text = caption
        else:
            e = sub(els, "enum", **attrs)
            for i, option in enumerate(("External", "Internal") if kind == "terminal" else ("Off", "On")):
                sub(sub(sub(e, "item", displayName="$(string." + option + ")"), "value"), "decimal", value=str(i))
            sub(form, "dropdownList", refId=field, defaultItem=str(opts)).text = caption

policy("Tenants", "Entra tenants (JSON)",
       "Replaces the Entra tenants in the current user's yubienroll.toml when Passkey Desk starts. Profiles are managed separately. Supply a JSON array with name, tenantId, clientId and redirectUri for each tenant. An explicit [] clears the Entra list. There is no tenant slot count. Do not enter credentials.",
       [("TenantsJson", "Tenant list (single-line JSON)", "text", 1048576)], "TenantsCategory")
policy("Activity", "Activity logging",
       "App setting only; does not change yubienroll.toml. Choose whether to keep activity, the retention in days and the log folder. Leave the folder blank to use YubiKey Desk\\ActivityLog under the current user's Documents folder, including redirected Documents. Paths may contain environment variables.",
       [("ActivityLogging", "Keep activity between sessions", "flag", 1), ("RetentionDays", "Retention (days)", "number", (1, 3650, 30)),
        ("ActivityFolder", "Activity log folder (blank = user default)", "folder", 32767)], "Logging")
policy("Registration", "Registration logging",
       "App setting only; does not change yubienroll.toml. Choose whether to save registration records and whether those records and CSV exports include newly generated initial PINs (off by default). Existing PINs are never captured. A blank folder uses YubiKey Desk\\RegistrationLog under the current user's Documents folder.",
       [("RegistrationLogging", "Save enrollment records", "flag", 0), ("SaveGeneratedPin", "Include generated initial PINs", "flag", 0),
        ("RegistrationFolder", "Registration log folder (blank = user default)", "folder", 32767)], "Logging")
policy("Terminal", "Terminal preference",
       "App setting only; does not change yubienroll.toml. Choose the preferred terminal. Enrollment with generated PIN capture still uses the in-app terminal. Only the terminal preference becomes read-only. Other settings, tenants and profiles remain editable unless managed separately.",
       [("EmbeddedTerminal", "Terminal", "terminal", 1)], "AppSettings")
for i in range(1, 6):
    name = "Profile" + str(i)
    fields = [("Name", "Profile name", "text", 80), ("Reset", "Reset FIDO before setup", "flag", 0),
              ("RandomPin", "Generate an initial PIN", "flag", 1),
              ("RandomPinLength", "Generated PIN length (0 = CLI default, otherwise 4–63)", "number", (0, 63, 6)),
              ("MinPinLength", "Minimum PIN length (0 = no override, otherwise 4–63)", "number", (0, 63, 0)),
              ("ForcePinChange", "Require PIN change at first use", "flag", 0),
              ("AlwaysUv", "Always require user verification", "flag", 0), ("RequireEa", "Require enterprise attestation", "flag", 0)]
    policy(name, "Configuration profile " + str(i),
           "Configure one reusable profile in the current user's yubienroll.toml. PIN generation and change on first use control key setup. Recording a generated PIN is a separate app setting under Logging. If any profile slot is configured, the configured slots together replace all profiles. Slots need not be consecutive. Reset is applied only during an operator-initiated key setup, not when this policy is loaded.",
           [(name+f, c, k, v) for f,c,k,v in fields], "Profiles")

for doc, target in [(admx, OUT / "PasskeyDesk.admx"), (adml, OUT / "en-US" / "PasskeyDesk.adml")]:
    target.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(doc, space="  ")
    target.write_bytes(ET.tostring(doc, encoding="utf-8", xml_declaration=True) + b"\n")
