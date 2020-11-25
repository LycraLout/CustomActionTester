﻿using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace Rappen.XTB.CAT
{
    public partial class CustomActionTester : IGitHubPlugin, IAboutPlugin, IShortcutReceiver, IMessageBusHost
    {
        #region Public Events

        public event EventHandler<MessageBusEventArgs> OnOutgoingMessage;

        #endregion Public Events

        #region Private Fields

        private const string aiEndpoint = "https://dc.services.visualstudio.com/v2/track";
        private const string aiKey = "eed73022-2444-45fd-928b-5eebd8fa46a6";    // jonas@rappen.net tenant, XrmToolBox
        private AppInsights ai = new AppInsights(aiEndpoint, aiKey, Assembly.GetExecutingAssembly(), "Custom Action Tester");
        private EntityMetadataProxy[] entities;

        #endregion Private Fields

        #region Public Properties

        public string RepositoryName => "CustomActionTester";

        public string UserName => "rappen";

        #endregion Public Properties

        #region Public Methods

        public void OnIncomingMessage(MessageBusEventArgs message)
        {
            // N/A
        }

        public void ReceiveKeyDownShortcut(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && btnExecute.Enabled)
            {
                btnExecute_Click(null, null);
            }
        }

        public void ReceiveKeyPressShortcut(KeyPressEventArgs e)
        {
            // Noop
        }

        public void ReceiveKeyUpShortcut(KeyEventArgs e)
        {
            // Noop
        }

        public void ReceivePreviewKeyDownShortcut(PreviewKeyDownEventArgs e)
        {
            // Noop
        }

        public void ShowAboutDialog()
        {
            var about = new About(this)
            {
                StartPosition = FormStartPosition.CenterParent
            };
            about.lblVersion.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            about.ShowDialog();
        }

        #endregion Public Methods

        #region Internal Methods

        internal void LogUse(string action, double? count = null, double? duration = null)
        {
            ai.WriteEvent(action, count, duration, HandleAIResult);
        }

        #endregion Internal Methods

        #region Private Methods

        private void ClearOutputParamValues()
        {
            var outputs = gridOutputParams.DataSource as IEnumerable<Entity>;
            outputs.ToList().ForEach(o => o.Attributes.Remove("value"));
        }

        private void ExecuteCA()
        {
            txtExecution.Text = string.Empty;
            ClearOutputParamValues();
            var request = new OrganizationRequest(txtMessageName.Text);
            foreach (var input in gridInputParams.DataSource as IEnumerable<Entity>)
            {
                if (input.TryGetAttributeValue("name", out string name))
                {
                    if (input.TryGetAttributeValue("rawvalue", out object value))
                    {
                        request[name] = value;
                    }
                    else if (input.TryGetAttributeValue("optional", out bool optional) && !optional)
                    {
                        MessageBox.Show($"Missing value for required parameter: {name}", "Execute Custom Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Executing Custom Action",
                Work = (worker, args) =>
                {
                    var sw = Stopwatch.StartNew();
                    var result = Service.Execute(request);
                    sw.Stop();
                    args.Result = new Tuple<OrganizationResponse, long>(result, sw.ElapsedMilliseconds);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else if (args.Result is Tuple<OrganizationResponse, long> response)
                    {
                        txtExecution.Text = $"{response.Item2} ms";
                        btnPTV.Enabled = true;
                        var outputparams = response.Item1.Results;
                        PopulateOutputParamValues(outputparams);
                    }
                }
            });
        }

        private void ExtractTypeInfo(EntityCollection records)
        {
            foreach (var record in records.Entities)
            {
                var attribute = record.Contains("parser") ? "parser" : "formatter";
                if (record.TryGetAttributeValue(attribute, out string parser))
                {
                    parser = parser.Split(',')[0];
                    while (parser.Contains("."))
                    {
                        parser = parser.Substring(parser.IndexOf('.') + 1);
                    }
                    record["type"] = parser;
                }
            }
            var otcrecords = records.Entities.Where(r => r.Contains("parameterbindinginformation"));
            var siblingrecords = new List<Entity>();
            foreach (var otcrecord in otcrecords)
            {
                var siblingrecord = records.Entities.FirstOrDefault(r => r["name"].ToString() == otcrecord["name"].ToString() && !r.Contains("parameterbindinginformation"));
                if (siblingrecord == null)
                {
                    continue;
                }
                var binding = otcrecord["parameterbindinginformation"].ToString();
                var otcstr = binding.Replace("OTC:", "").Trim();
                if (int.TryParse(otcstr, out int otc))
                {
                    if (entities.FirstOrDefault(e => e.Metadata.ObjectTypeCode == otc) is EntityMetadataProxy entity)
                    {
                        otcrecord["type"] = otcrecord["type"].ToString() + " " +
                            (entity.Metadata?.DisplayName?.UserLocalizedLabel?.Label ?? entity.Metadata.LogicalName);
                        otcrecord["entity"] = entity;
                    }
                }
                siblingrecords.Add(siblingrecord);
            }
            siblingrecords.ForEach(s => records.Entities.Remove(s));
        }

        private void FormatResultDetail()
        {
            var value = GetResultDetailValue();
            if (value is string strvalue)
            {
                if (!string.IsNullOrEmpty(strvalue))
                {
                    try
                    {
                        if (rbFormatJSON.Checked)
                        {
                            var parsedJson = JToken.Parse(strvalue);
                            strvalue = parsedJson.ToString(Newtonsoft.Json.Formatting.Indented);
                        }
                        else if (rbFormatXML.Checked)
                        {
                            var string_writer = new StringWriter();
                            var xml_text_writer = new XmlTextWriter(string_writer);
                            xml_text_writer.Formatting = System.Xml.Formatting.Indented;
                            strvalue = string_writer.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        strvalue = $"Error: {ex}";
                    }
                }
                txtResultDetail.Text = strvalue;
            }
        }

        private void FormatResultDetailDefault()
        {
            var value = GetResultDetailValue();
            if (value is string strvalue)
            {
                if (string.IsNullOrEmpty(strvalue))
                {
                    return;
                }
                if (strvalue.StartsWith("{"))
                {
                    rbFormatJSON.Checked = true;
                }
                else if (strvalue.StartsWith("<"))
                {
                    rbFormatXML.Checked = true;
                }
            }
            FormatResultDetail();
        }

        private void GetCustomActions(Entity solution)
        {
            var qx = new QueryExpression("workflow");
            qx.ColumnSet.AddColumns("name", "uniquename", "createdby", "primaryentity", "scope", "mode", "ismanaged", "iscustomizable", "istransacted", "iscustomprocessingstepallowedforotherpublishers", "inputparameters", "description");
            qx.AddOrder("ismanaged", OrderType.Descending);
            qx.AddOrder("name", OrderType.Ascending);
            qx.Criteria.AddCondition("category", ConditionOperator.Equal, 3);
            qx.Criteria.AddCondition("type", ConditionOperator.Equal, 1);
            qx.Criteria.AddCondition("componentstate", ConditionOperator.Equal, 0);
            qx.Criteria.AddCondition("statuscode", ConditionOperator.Equal, 2);
            var qxsdk = qx.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.LeftOuter);
            qxsdk.EntityAlias = "M";
            qxsdk.Columns.AddColumns("name", "workflowsdkstepenabled");
            if (solution != null)
            {
                var solcomp = qx.AddLink("solutioncomponent", "workflowid", "objectid");
                solcomp.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
            }

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Getting Custom Actions",
                Work = (worker, args) =>
                {
                    args.Result = Service.RetrieveMultiple(qx);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else if (args.Result is EntityCollection actions)
                    {
                        cmbCustomActions.DataSource = actions;
                    }
                }
            });
        }

        private void GetInputParams(Entity ca)
        {
            if (ca == null)
            {
                gridInputParams.DataSource = null;
                gridOutputParams.DataSource = null;
                return;
            }
            var qx = new QueryExpression("sdkmessagerequestfield");
            qx.Distinct = true;
            qx.ColumnSet.AddColumns("name", "position", "parameterbindinginformation", "optional", "parser", "fieldmask");
            qx.AddOrder("position", OrderType.Ascending);
            var req = qx.AddLink("sdkmessagerequest", "sdkmessagerequestid", "sdkmessagerequestid");
            var pair = req.AddLink("sdkmessagepair", "sdkmessagepairid", "sdkmessagepairid");
            var msg = pair.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid");
            var wf = msg.AddLink("workflow", "sdkmessageid", "sdkmessageid");
            wf.LinkCriteria.AddCondition("workflowid", ConditionOperator.Equal, ca.Id);
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading Input Parameters",
                Work = (worker, args) =>
                {
                    args.Result = Service.RetrieveMultiple(qx);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (args.Result is EntityCollection inputs)
                    {
                        ExtractTypeInfo(inputs);
                        gridInputParams.DataSource = inputs;
                        gridInputParams.AutoResizeColumns();
                        GetOutputParams(ca);
                    }
                }
            });
        }

        private void GetInputParamValue(xrmtb.XrmToolBox.Controls.CRMRecordEventArgs e)
        {
            if (inputdlg == null)
            {
                inputdlg = new InputValue(Service, entities);
            }
            var dlgresult = inputdlg.ShowDialog(e.Entity, this);
            if (dlgresult == DialogResult.Cancel)
            {
                return;
            }
            if (dlgresult == DialogResult.OK && inputdlg.Result != null)
            {
                e.Entity["rawvalue"] = inputdlg.Result;
                e.Entity["value"] = inputdlg.FormattedResult;
            }
            else if (dlgresult == DialogResult.Ignore)
            {
                e.Entity.Attributes.Remove("value");
                e.Entity.Attributes.Remove("rawvalue");
            }
            gridInputParams.Refresh();
            gridInputParams.AutoResizeColumns();
            btnExecute.Enabled = ReadyToExecute();
        }

        private void GetOutputParams(Entity ca)
        {
            if (ca == null)
            {
                gridOutputParams.DataSource = null;
                return;
            }
            var qx = new QueryExpression("sdkmessageresponsefield");
            qx.Distinct = true;
            qx.ColumnSet.AddColumns("name", "position", "parameterbindinginformation", "formatter", "publicname");
            qx.AddOrder("position", OrderType.Ascending);
            var resp = qx.AddLink("sdkmessageresponse", "sdkmessageresponseid", "sdkmessageresponseid");
            var req = resp.AddLink("sdkmessagerequest", "sdkmessagerequestid", "sdkmessagerequestid");
            var pair = req.AddLink("sdkmessagepair", "sdkmessagepairid", "sdkmessagepairid");
            var msg = pair.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid");
            var wf = msg.AddLink("workflow", "sdkmessageid", "sdkmessageid");
            wf.LinkCriteria.AddCondition("workflowid", ConditionOperator.Equal, ca.Id);
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading Output Parameters",
                Work = (worker, args) =>
                {
                    args.Result = Service.RetrieveMultiple(qx);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else if (args.Result is EntityCollection outputs)
                    {
                        ExtractTypeInfo(outputs);
                        gridOutputParams.DataSource = outputs;
                        gridOutputParams.AutoResizeColumns();
                    }
                }
            });
        }

        private object GetResultDetailValue()
        {
            var record = gridOutputParams.SelectedCellRecords.FirstOrDefault();
            if (record == null || !record.TryGetAttributeValue("rawvalue", out object result))
            {
                txtResultDetail.Text = string.Empty;
                return null;
            }
            if (result is string)
            {
                return result;
            }
            return ValueToString(result, true, false, true, Service);
        }

        private void GetSolutions(bool managed, bool invisible)
        {
            var qx = new QueryExpression("solution");
            qx.Distinct = true;
            qx.ColumnSet.AddColumns("uniquename", "friendlyname", "version", "solutionid");
            qx.AddOrder("friendlyname", OrderType.Ascending);
            qx.Criteria.AddCondition("ismanaged", ConditionOperator.Equal, managed);
            qx.Criteria.AddCondition("isvisible", ConditionOperator.Equal, !invisible);
            var solcomp = qx.AddLink("solutioncomponent", "solutionid", "solutionid");
            solcomp.Columns.AddColumns("componenttype");
            var wf = solcomp.AddLink("workflow", "objectid", "workflowid");
            wf.LinkCriteria.AddCondition("category", ConditionOperator.Equal, 3);
            wf.LinkCriteria.AddCondition("type", ConditionOperator.Equal, 1);
            wf.LinkCriteria.AddCondition("componentstate", ConditionOperator.Equal, 0);
            wf.LinkCriteria.AddCondition("statuscode", ConditionOperator.Equal, 2);
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Getting Solutions",
                Work = (worker, args) =>
                {
                    args.Result = Service.RetrieveMultiple(qx);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else if (args.Result is EntityCollection solutions)
                    {
                        cmbSolution.DataSource = solutions;
                    }
                }
            });
        }

        private void HandleAIResult(string result)
        {
            if (!string.IsNullOrEmpty(result))
            {
                LogError("Failed to write to Application Insights:\n{0}", result);
            }
        }

        private void LoadEntities()
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading entities...",
                Work = (worker, eventargs) =>
                {
                    eventargs.Result = MetadataHelper.LoadEntities(Service);
                },
                PostWorkCallBack = (completedargs) =>
                {
                    if (completedargs.Error != null)
                    {
                        MessageBox.Show(completedargs.Error.Message);
                        return;
                    }
                    if (completedargs.Result is RetrieveMetadataChangesResponse resp)
                    {
                        entities = resp.EntityMetadata
                            .Where(e => e.IsCustomizable.Value == true && e.IsIntersect.Value != true)
                            .Select(m => new EntityMetadataProxy(m))
                            .OrderBy(e => e.ToString()).ToArray();
                    }
                }
            });
        }

        private void PopulateOutputParamValues(ParameterCollection outputparams)
        {
            foreach (var result in outputparams)
            {
                var outputs = gridOutputParams.DataSource as IEnumerable<Entity>;
                var output = outputs.FirstOrDefault(o => o["name"].ToString().Equals(result.Key));
                if (output != null)
                {
                    var rawvalue = result.Value;
                    var value = rawvalue;
                    output["rawvalue"] = rawvalue;
                    if (rawvalue is Money money)
                    {
                        value = money.Value;
                    }
                    else if (rawvalue is OptionSetValue osv)
                    {
                        value = osv.Value;
                    }
                    else if (rawvalue is Entity entity)
                    {
                        txtCDSDataHelper.Entity = entity;
                        value = txtCDSDataHelper.Text;
                    }
                    else if (rawvalue is EntityReference entref)
                    {
                        txtCDSDataHelper.EntityReference = entref;
                        value = txtCDSDataHelper.Text;
                    }
                    output["value"] = value;
                }
            }
            gridOutputParams.Refresh();
            gridOutputParams.AutoResizeColumns();
            FormatResultDetailDefault();
        }

        private bool ReadyToExecute()
        {
            if (cmbCustomActions.SelectedEntity == null || !(gridInputParams.DataSource is IEnumerable<Entity> inputparams))
            {
                return false;
            }
            foreach (var inputparam in inputparams)
            {
                if (inputparam.TryGetAttributeValue("optional", out bool optional) && !optional &&
                    !inputparam.TryGetAttributeValue("rawvalue", out object _))
                {
                    return false;
                }
            }
            return true;
        }

        private void SetCustomAction(Entity ca)
        {
            txtUniqueName.Entity = ca;
            txtMessageName.Entity = ca;
            txtCreatedBy.Entity = ca;
            txtExecution.Text = string.Empty;
            GetInputParams(ca);
            btnExecute.Enabled = ReadyToExecute();
        }

        private void TraceLastExecution()
        {
            OnOutgoingMessage(this, new MessageBusEventArgs("Plugin Trace Viewer", true) { TargetArgument = $"Message={txtMessageName.Text}" });
        }

        public static string ValueToString(object value, bool attributetypes, bool convertqueries, bool expandcollections, IOrganizationService service, int indent = 1)
        {
            var indentstring = new string(' ', indent * 2);
            if (value == null)
            {
                return $"{indentstring}<null>";
            }
            else if (value is EntityCollection collection)
            {
                var result = $"{collection.EntityName} collection\n  Records: {collection.Entities.Count}\n  TotalRecordCount: {collection.TotalRecordCount}\n  MoreRecords: {collection.MoreRecords}\n  PagingCookie: {collection.PagingCookie}";
                if (expandcollections)
                {
                    result += $"\n{indentstring}  {string.Join($"\n{indentstring}", collection.Entities.Select(e => ValueToString(e, attributetypes, convertqueries, expandcollections, service, indent + 1)))}";
                }
                return result;
            }
            else if (value is Entity entity)
            {
                var keylen = entity.Attributes.Count > 0 ? entity.Attributes.Max(p => p.Key.Length) : 50;
                return $"{entity.LogicalName} {entity.Id}\n{indentstring}" + string.Join($"\n{indentstring}", entity.Attributes.OrderBy(a => a.Key).Select(a => $"{a.Key}{new string(' ', keylen - a.Key.Length)} = {ValueToString(a.Value, attributetypes, convertqueries, expandcollections, service, indent + 1)}"));
            }
            else if (value is ColumnSet columnset)
            {
                var columnlist = new List<string>(columnset.Columns);
                columnlist.Sort();
                return $"\n{indentstring}" + string.Join($"\n{indentstring}", columnlist);
            }
            else if (value is FetchExpression fetchexpression)
            {
                return $"{value}\n{indentstring}{fetchexpression.Query}";
            }
            else
            {
                var result = string.Empty;
                if (value is EntityReference entityreference)
                {
                    result = $"{entityreference.LogicalName} {entityreference.Id} {entityreference.Name}";
                }
                else if (value is OptionSetValue optionsetvalue)
                {
                    result = optionsetvalue.Value.ToString();
                }
                else if (value is Money money)
                {
                    result = money.Value.ToString();
                }
                else
                {
                    result = value.ToString().Replace("\n", $"\n  {indentstring}");
                }
                return result + (attributetypes ? $" \t({value.GetType()})" : "");
            }
        }

        #endregion Private Methods
    }
}
