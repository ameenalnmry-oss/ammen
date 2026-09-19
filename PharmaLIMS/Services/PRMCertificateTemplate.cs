using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

#nullable disable

namespace PharmaLIMS
{
    public static class PRMCertificateTemplate
    {
        public static string Build(
            DataRow sample,
            DataTable results,
            DataRow cert,
            DataTable electronicSignatures)
        {
            string category = S(sample, "SampleCategory");

            string mainTitle = GetMainDocumentTitle(category);
            string subTitle = GetDocumentSubTitle(category);
            string documentLabel = GetDocumentNumberLabel(category);
            string documentTerm = GetDocumentTerm(category);

            string storedTitle = S(cert, "ReportTitle");
            if (string.IsNullOrWhiteSpace(storedTitle))
                storedTitle = GetReportTitle(category);

            string certificateNo = S(cert, "CertificateNumber");
            string revisionNo = S(cert, "RevisionNo");
            string issueDate = FormatDateTime(cert, "IssueDate");
            string issuedBy = S(cert, "IssuedBy");
            string overall = S(sample, "ResultInterpretation");
            string verificationCode = S(cert, "VerificationCode");
            string reportHash = S(cert, "ReportHash");
            string logoDataUri = GetLogoDataUri();
            DataRow entrySignature = FindLatestSignature(electronicSignatures, "Result Entry");
            DataRow reviewSignature = FindLatestSignature(electronicSignatures, "Review");
            DataRow approvalSignature = FindLatestSignature(electronicSignatures, "Approval");
            DataRow issueSignature = FindLatestSignature(electronicSignatures, "Certificate Issuance", "Certificate Reissue");
            issuedBy = FirstAvailable(S(issueSignature, "SignerDisplayName"), issuedBy);
            bool hasCompleteSignatureChain = entrySignature != null && reviewSignature != null && approvalSignature != null && issueSignature != null;

            StringBuilder sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='utf-8'>");
            sb.AppendLine("<title>" + H(storedTitle) + "</title>");

            sb.AppendLine("<style>");
            sb.AppendLine("@page{size:A4;margin:0;}");
            sb.AppendLine("*{box-sizing:border-box;}");
            sb.AppendLine("html,body{width:210mm;min-height:297mm;margin:0;padding:0;background:#ffffff;}");
            sb.AppendLine("body{font-family:Arial,sans-serif;color:#1E293B;font-size:10.5px;padding:10mm;}");
            sb.AppendLine(".page{width:190mm;min-height:277mm;margin:0 auto;padding:0;border:1.4px solid #1E3A5F;border-radius:0;overflow:hidden;background:#ffffff;}");
            sb.AppendLine(".header{background:#1E3A5F;color:#ffffff;padding:11px 12px;display:grid;grid-template-columns:150px 1fr 175px;align-items:center;gap:12px;}");
            sb.AppendLine(".logoBox{background:#ffffff;border-radius:6px;padding:6px;height:52px;display:flex;align-items:center;justify-content:center;}");
            sb.AppendLine(".logoBox img{max-width:148px;max-height:40px;}");
            sb.AppendLine(".logoText{font-size:22px;font-weight:900;color:#253348;letter-spacing:.5px;}");
            sb.AppendLine(".company{text-align:center;}");
            sb.AppendLine(".companyName{font-size:17.5px;font-weight:800;letter-spacing:.2px;text-transform:uppercase;}");
            sb.AppendLine(".dept{font-size:11px;margin-top:4px;color:#CBD5E1;font-weight:600;}");
            sb.AppendLine(".docMeta{font-family:Arial,sans-serif;font-size:9px;line-height:1.55;text-align:right;color:#E2E8F0;}");
            sb.AppendLine(".titleBlock{text-align:center;padding:10px 12px 8px 12px;border-bottom:2px solid #E2E8F0;}");
            sb.AppendLine(".certTitle{font-size:20px;font-weight:900;letter-spacing:.3px;text-transform:uppercase;color:#1E293B;}");
            sb.AppendLine(".certSubTitle{font-size:13px;font-weight:700;color:#334155;margin-top:4px;text-transform:uppercase;}");
            sb.AppendLine(".methodLine{font-size:10px;color:#64748B;margin-top:3px;}");
            sb.AppendLine(".content{padding:10px 12px 12px 12px;}");
            sb.AppendLine(".certInfo{display:grid;grid-template-columns:1fr 238px;gap:10px;margin-bottom:10px;}");
            sb.AppendLine(".infoBox{border:1px solid #d9e4f2;border-radius:8px;background:#ffffff;overflow:hidden;}");
            sb.AppendLine(".rightBox{background:#FFFFFF;border:1px solid #B8C7D9;border-radius:5px;padding:0;font-size:9.5px;line-height:1.55;color:#475569;overflow:hidden;}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;}");
            sb.AppendLine("th,td{border:1px solid #cbd5e1;padding:6px 7px;vertical-align:top;}");
            sb.AppendLine("th{background:#EAF0F7;text-align:left;font-weight:800;color:#0f172a;}");
            sb.AppendLine(".sampleInfo th{width:135px;}");
            sb.AppendLine(".sampleInfo td{width:185px;}");
            sb.AppendLine(".sectionTitle{font-size:13px;font-weight:900;margin:11px 0 6px 0;color:#1E293B;}");
            sb.AppendLine(".results th{background:#1E3A5F;font-size:10px;color:#FFFFFF;text-align:center;}");
            sb.AppendLine(".results td{font-size:10px;vertical-align:middle;}");
            sb.AppendLine(".conform{font-weight:900;color:#047857;}");
            sb.AppendLine(".dnc{font-weight:900;color:#b91c1c;}");
            sb.AppendLine(".review{font-weight:900;color:#b45309;}");
            sb.AppendLine(".conclusion{border:1.4px solid #CBD5E1;background:#F8FAFC;border-radius:6px;padding:10px;margin-top:9px;text-align:center;}");
            sb.AppendLine(".conclusionTitle{font-weight:900;margin-bottom:5px;letter-spacing:.3px;}");
            sb.AppendLine(".conclusionText{font-size:11px;font-weight:800;text-transform:uppercase;}");
            sb.AppendLine(".conclusion.conclusionConform{border-color:#10B981;background:#ECFDF5;color:#047857;}");
            sb.AppendLine(".conclusion.conclusionDnc{border-color:#DC2626;background:#FEF2F2;color:#991B1B;}");
            sb.AppendLine(".conclusion.conclusionReview{border-color:#D97706;background:#FFFBEB;color:#92400E;}");
            sb.AppendLine(".results tr{page-break-inside:avoid;break-inside:avoid;}");
            sb.AppendLine(".results thead{display:table-header-group;}");
            sb.AppendLine(".statement{font-size:9px;color:#475569;text-align:center;margin:8px 0;border-top:1px solid #e2e8f0;border-bottom:1px solid #e2e8f0;padding:6px;}");
            sb.AppendLine(".signatures{margin-top:8px;display:grid;grid-template-columns:repeat(4,1fr);gap:8px;}");
            sb.AppendLine(".sigBox{border:1px solid #CBD5E1;border-radius:6px;padding:8px;text-align:center;min-height:86px;background:#ffffff;}");
            sb.AppendLine(".sigLine{border-top:1.4px solid #334155;margin:14px 18px 5px 18px;}");
            sb.AppendLine(".sigTitle{font-size:9.3px;color:#64748b;}");
            sb.AppendLine(".sigName{font-weight:900;font-size:10px;margin-top:3px;}");
            sb.AppendLine(".sigRole{font-size:8.5px;color:#64748b;margin-top:2px;}");
            sb.AppendLine(".verify{margin-top:8px;display:grid;grid-template-columns:1fr 210px;gap:8px;align-items:end;}");
            sb.AppendLine(".hashBox,.verifyBox{border:1px solid #d9e4f2;border-radius:7px;padding:8px;background:#ffffff;font-size:8.6px;color:#334155;}");
            sb.AppendLine(".footer{font-size:8px;color:#64748b;text-align:center;margin-top:7px;}");
            sb.AppendLine(".watermark{position:fixed;left:0;right:0;top:118mm;text-align:center;font-size:76px;color:#0f172a;opacity:.035;font-weight:900;transform:rotate(-22deg);z-index:-1;}");
            sb.AppendLine("@media print{html,body{margin:0!important}.page{margin:0;border:1.4px solid #1E3A5F}.noPrint{display:none}}");
            sb.AppendLine("</style>");

            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<div class='watermark'>MEDICA</div>");
            sb.AppendLine("<div class='page'>");

            sb.AppendLine("<div class='header'>");
            sb.AppendLine("<div class='logoBox'>");

            if (!string.IsNullOrWhiteSpace(logoDataUri))
                sb.AppendLine("<img src='" + logoDataUri + "' alt='medica logo'>");
            else
                sb.AppendLine("<div class='logoText'>medica</div>");

            sb.AppendLine("</div>");

            sb.AppendLine(
                "<div class='company'>" +
                "<div class='companyName'>MEDICA PHARMACEUTICAL INDUSTRY</div>" +
                "<div class='dept'>Microbiology Department</div>" +
                "</div>");

            sb.AppendLine(
                "<div class='docMeta'>" +
                "Form No: MQC-F-PRM-001<br>" +
                "Version: 01<br>" +
                "Controlled Electronic Document" +
                "</div>");

            sb.AppendLine("</div>");

            sb.AppendLine("<div class='titleBlock'>");
            sb.AppendLine("<div class='certTitle'>" + H(mainTitle) + "</div>");
            sb.AppendLine("<div class='certSubTitle'>" + H(subTitle) + "</div>");
            sb.AppendLine("<div class='methodLine'>Analysis according to approved microbiology specification and PharmaLIMS workflow</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class='content'>");

            sb.AppendLine("<div class='certInfo'>");
            sb.AppendLine("<div class='infoBox'>");
            sb.AppendLine("<table class='sampleInfo'>");

            AddRow(
                sb,
                "Sample Number",
                S(sample, "SampleNumber"),
                "Sample Type",
                category);

            AddRow(
                sb,
                "Item / Product",
                GetItemName(sample),
                "Lot / Batch",
                GetLotOrBatch(sample));

            AddRow(
                sb,
                "Specification No.",
                S(sample, "SpecificationNo"),
                "Specification Version",
                string.IsNullOrWhiteSpace(S(sample, "SpecificationVersionNo"))
                    ? string.Empty
                    : "v" + S(sample, "SpecificationVersionNo"));

            AddRow(
                sb,
                "Sampled By",
                FirstAvailable(S(sample, "SampledByDisplay"), S(sample, "SampledBy")),
                "Sampling Date / Time",
                FormatDateTime(sample, "SampleDateTime"));

            AddRow(
                sb,
                "Status",
                S(sample, "SampleStatus"),
                "Analysis Started",
                FormatDateTime(sample, "AnalysisStartedDate"));

            AddRow(
                sb,
                "Analysis Completed",
                FormatDateTime(sample, "AnalysisCompletedDate"),
                "Reviewed Date / Time",
                FormatDateTime(sample, "ReviewedDate"));

            AddRow(
                sb,
                "Approved Date / Time",
                FormatDateTime(sample, "ApprovedDate"),
                "Overall Result",
                S(sample, "ResultInterpretation"));

            AppendCategoryTraceabilityRows(sb, sample, category);

            sb.AppendLine("</table>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div class='rightBox'>");
            sb.AppendLine("<div style='background:#1E3A5F;color:#fff;font-weight:800;text-align:center;padding:6px 8px;letter-spacing:.2px'>DOCUMENT INFORMATION</div>");
            sb.AppendLine("<div style='padding:8px 9px'>");
            sb.AppendLine(
                "<b>" + H(documentLabel) + ":</b> " +
                "<span style='font-weight:800'>" + H(certificateNo) + "</span><br>");

            sb.AppendLine("<b>Issue Date:</b> " + H(issueDate) + "<br>");
            sb.AppendLine("<b>Revision No:</b> " + H(revisionNo) + "<br>");
            sb.AppendLine("<b>Issued By:</b> " + H(issuedBy) + "<br>");
            sb.AppendLine("<b>" + H(documentTerm) + " Status:</b> " + H(S(cert, "CertificateStatus")) + "<br>");
            sb.AppendLine("<b>Verification Code:</b> " + H(verificationCode) + "<br>");
            sb.AppendLine("<b>Data Integrity:</b> SHA-256 hash recorded in the verification section below");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</div>");

            sb.AppendLine("<div class='sectionTitle'>Microbiological Test Results</div>");

            sb.AppendLine("<table class='results'><thead>");
            sb.AppendLine(
                "<tr>" +
                "<th style='width:32%'>Test Name</th>" +
                "<th style='width:23%'>Limit / Specification</th>" +
                "<th style='width:16%'>Result</th>" +
                "<th style='width:10%'>Unit</th>" +
                "<th style='width:12%'>Conformity</th>" +
                "<th style='width:7%'>Remarks</th>" +
                "</tr>");
            sb.AppendLine("</thead><tbody>");

            foreach (DataRow r in results.Rows)
            {
                string interpretation = S(r, "Interpretation");
                string css = GetInterpretationCss(interpretation);

                sb.AppendLine("<tr>");
                sb.AppendLine("<td>" + H(S(r, "TestName")) + "</td>");
                sb.AppendLine("<td>" + H(S(r, "SpecificationText")) + "</td>");
                sb.AppendLine("<td style='text-align:center;font-weight:700'>" + H(S(r, "ResultValue")) + "</td>");
                sb.AppendLine("<td style='text-align:center'>" + H(S(r, "Unit")) + "</td>");
                sb.AppendLine("<td style='text-align:center' class='" + css + "'>" + H(FormatConformity(interpretation)) + "</td>");
                sb.AppendLine("<td>" + H(S(r, "Remarks")) + "</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table>");

            sb.AppendLine("<div class='conclusion " + GetConclusionCss(overall) + "'>");
            sb.AppendLine("<div class='conclusionTitle'>FINAL CONCLUSION</div>");
            sb.AppendLine("<div class='conclusionText'>" + H(BuildFinalConclusion(overall)) + "</div>");
            sb.AppendLine("</div>");

            sb.AppendLine(
                "<div class='statement'>" +
                (hasCompleteSignatureChain
                    ? "This " + H(documentTerm.ToLowerInvariant()) + " is electronically generated and electronically signed. It is valid without handwritten signature.<br>"
                    : "This " + H(documentTerm.ToLowerInvariant()) + " is electronically generated. The electronic-signature chain is incomplete; controlled use is prohibited.<br>") +
                "Generated from the immutable issue record at: " + H(issueDate) +
                " | Generated by PharmaLIMS" +
                "</div>");

            sb.AppendLine("<div class='signatures'>");

            AppendSignatureBox(
                sb,
                "Entered By",
                FirstAvailable(S(entrySignature, "SignerDisplayName"), S(entrySignature, "SignedBy")),
                S(entrySignature, "UserRole"),
                "Result Entry");

            AppendSignatureBox(
                sb,
                "Reviewed By",
                FirstAvailable(S(reviewSignature, "SignerDisplayName"), S(reviewSignature, "SignedBy")),
                S(reviewSignature, "UserRole"),
                "Technical Review");

            AppendSignatureBox(
                sb,
                "Approved By",
                FirstAvailable(S(approvalSignature, "SignerDisplayName"), S(approvalSignature, "SignedBy")),
                S(approvalSignature, "UserRole"),
                "QA Approval");

            AppendSignatureBox(
                sb,
                "Issued By",
                FirstAvailable(S(issueSignature, "SignerDisplayName"), S(issueSignature, "SignedBy")),
                S(issueSignature, "UserRole"),
                documentTerm + " Issuance");

            sb.AppendLine("</div>");

            sb.AppendLine("<div class='verify'>");

            sb.AppendLine(
                "<div class='hashBox'>" +
                "<b>Report Data Hash (SHA-256):</b><br>" +
                H(reportHash) +
                "</div>");

            sb.AppendLine(
                "<div class='verifyBox'>" +
                "<b>" + H(documentTerm) + " Verification</b><br>" +
                "Verification Code: " + H(verificationCode) + "<br>" +
                "This document shall not be reproduced except in full." +
                "</div>");

            sb.AppendLine("</div>");

            sb.AppendLine(
                "<div class='footer'>" +
                "This document reports microbiological laboratory results only. " +
                "Final batch, material, or product disposition remains subject to the approved site quality system." +
                "</div>");

            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static void AppendCategoryTraceabilityRows(StringBuilder sb, DataRow sample, string category)
        {
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                AddRow(sb, "Material Code", S(sample, "MaterialCode"), "Manufacturer", S(sample, "Manufacturer"));
                AddRow(sb, "Supplier / GRN", JoinValues(S(sample, "Supplier"), S(sample, "GRNNo")),
                    "Storage / Retest", JoinValues(S(sample, "StorageCondition"), FormatDateOnly(sample, "RetestDate")));
                return;
            }

            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
            {
                AddRow(sb, "Product Code / Study", JoinValues(S(sample, "ProductCode"), S(sample, "ProductionStage")),
                    "Pull Point / Storage", JoinValues(S(sample, "SampleSource"), S(sample, "StorageCondition")));
                AddRow(sb, "Chamber No.", S(sample, "StabilityChamberNo"), "Protocol No.", S(sample, "StabilityProtocolNo"));
                return;
            }

            AddRow(sb, "Product Code / Dosage Form", JoinValues(S(sample, "ProductCode"), S(sample, "DosageForm")),
                "Stage / Source", JoinValues(S(sample, "ProductionStage"), S(sample, "SampledFrom")));
            AddRow(sb, "Machine / Line", S(sample, "MachineLineNo"),
                "Manufacturing Date", FormatDateOnly(sample, "ManufacturingDate"));
            AddRow(sb, "Packaging Date", FormatDateOnly(sample, "PackagingDate"),
                "Expiry Date", FormatDateOnly(sample, "ExpiryDate"));
        }

        private static string JoinValues(params string[] values)
        {
            if (values == null)
                return string.Empty;
            return string.Join(" / ", Array.FindAll(values, value => !string.IsNullOrWhiteSpace(value)));
        }

        private static DataRow FindLatestSignature(DataTable signatures, params string[] actionTypes)
        {
            if (signatures == null || signatures.Rows.Count == 0 || actionTypes == null)
                return null;

            for (int i = signatures.Rows.Count - 1; i >= 0; i--)
            {
                DataRow row = signatures.Rows[i];
                string action = S(row, "ActionType");
                foreach (string actionType in actionTypes)
                {
                    if (action.Equals(actionType, StringComparison.OrdinalIgnoreCase))
                        return row;
                }
            }

            return null;
        }

        public static string SafeFileName(string value)
        {
            value ??= string.Empty;

            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            return value;
        }

        private static string GetMainDocumentTitle(string category)
        {
            if (category.Equals(
                "Raw Material",
                StringComparison.OrdinalIgnoreCase))
            {
                return "CERTIFICATE OF ANALYSIS";
            }

            if (category.Equals(
                "Finished Product",
                StringComparison.OrdinalIgnoreCase))
            {
                return "CERTIFICATE OF ANALYSIS";
            }

            if (category.Equals(
                "Stability",
                StringComparison.OrdinalIgnoreCase))
            {
                return "MICROBIOLOGICAL TEST REPORT";
            }

            return "MICROBIOLOGICAL TEST REPORT";
        }

        private static string GetDocumentSubTitle(string category)
        {
            if (category.Equals(
                "Raw Material",
                StringComparison.OrdinalIgnoreCase))
            {
                return "RAW MATERIAL";
            }

            if (category.Equals(
                "Finished Product",
                StringComparison.OrdinalIgnoreCase))
            {
                return "FINISHED PRODUCT";
            }

            if (category.Equals(
                "Stability",
                StringComparison.OrdinalIgnoreCase))
            {
                return "STABILITY STUDY";
            }

            return "IN-PROCESS PRODUCT";
        }

        private static string GetDocumentNumberLabel(string category)
        {
            if (IsCertificateCategory(category))
                return "Certificate No.";

            return "Report No.";
        }

        private static string GetDocumentTerm(string category)
        {
            return IsCertificateCategory(category)
                ? "Certificate"
                : "Report";
        }

        private static bool IsCertificateCategory(string category)
        {
            return category.Equals(
                       "Raw Material",
                       StringComparison.OrdinalIgnoreCase)
                   ||
                   category.Equals(
                       "Finished Product",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLogoDataUri()
        {
            try
            {
                string[] possiblePaths =
                {
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "medica-logo.png"),

                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Resources",
                        "medica-logo.png"),

                    Path.Combine(
                        Environment.CurrentDirectory,
                        "medica-logo.png"),

                    Path.Combine(
                        Environment.CurrentDirectory,
                        "Resources",
                        "medica-logo.png")
                };

                foreach (string path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        return "data:image/png;base64," +
                               Convert.ToBase64String(
                                   File.ReadAllBytes(path));
                    }
                }
            }
            catch
            {
                // Report generation must continue if the logo is unavailable.
            }

            return string.Empty;
        }

        private static string GetItemName(DataRow row)
        {
            string category = S(row, "SampleCategory");

            return category.Equals(
                "Raw Material",
                StringComparison.OrdinalIgnoreCase)
                ? S(row, "MaterialName")
                : S(row, "ProductName");
        }

        private static string GetLotOrBatch(DataRow row)
        {
            string category = S(row, "SampleCategory");

            if (category.Equals(
                "Raw Material",
                StringComparison.OrdinalIgnoreCase))
            {
                string manufacturerLot =
                    S(row, "ManufacturerLotNo");

                return string.IsNullOrWhiteSpace(manufacturerLot)
                    ? S(row, "SupplierLotNo")
                    : manufacturerLot;
            }

            return S(row, "BatchNo");
        }

        private static string GetReportTitle(string category)
        {
            if (category.Equals(
                "Raw Material",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Raw Material Microbiological Certificate of Analysis";
            }

            if (category.Equals(
                "Finished Product",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Finished Product Microbiological Certificate of Analysis";
            }

            if (category.Equals(
                "Stability",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Stability Microbiological Test Report";
            }

            return "In-Process Microbiological Test Report";
        }

        private static string GetInterpretationCss(string interpretation)
        {
            if (interpretation.Equals(
                "Does Not Conform",
                StringComparison.OrdinalIgnoreCase))
            {
                return "dnc";
            }

            if (interpretation.Equals(
                    "Check Required",
                    StringComparison.OrdinalIgnoreCase)
                ||
                interpretation.Equals(
                    "Not Tested",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "review";
            }

            return "conform";
        }

        private static string ShortHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Length <= 18
                ? value
                : value.Substring(0, 18) + "...";
        }

        private static string FormatConformity(string value)
        {
            if (value.Equals(
                "Conforms",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Conform";
            }

            return value;
        }

        private static string GetConclusionCss(string overall)
        {
            if (overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
                return "conclusionDnc";
            if (overall.Equals("Check Required", StringComparison.OrdinalIgnoreCase) ||
                overall.Equals("Not Tested", StringComparison.OrdinalIgnoreCase))
                return "conclusionReview";
            return "conclusionConform";
        }

        private static string BuildFinalConclusion(string overall)
        {
            if (overall.Equals(
                "Conforms",
                StringComparison.OrdinalIgnoreCase))
            {
                return "The tested sample conforms to the approved microbiological specification.";
            }

            if (overall.Equals(
                "Does Not Conform",
                StringComparison.OrdinalIgnoreCase))
            {
                return "The tested sample does not conform to the approved microbiological specification. The related quality-event investigation and disposition are retained in controlled PharmaLIMS records. This microbiology document does not constitute final batch/material disposition.";
            }

            if (overall.Equals(
                "Check Required",
                StringComparison.OrdinalIgnoreCase))
            {
                return "The tested sample requires technical review before final microbiological conclusion.";
            }

            return "The tested sample microbiological status is " +
                   overall +
                   ".";
        }

        private static void AppendSignatureBox(
            StringBuilder sb,
            string title,
            string name,
            string role,
            string meaning)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = GetPendingSignatureText(title);

            if (string.IsNullOrWhiteSpace(role))
                role = "Not assigned";

            sb.AppendLine("<div class='sigBox'>");
            sb.AppendLine("<div class='sigLine'></div>");
            sb.AppendLine("<div class='sigTitle'>Electronically signed by</div>");
            sb.AppendLine("<div class='sigName'>" + H(name) + "</div>");
            sb.AppendLine("<div class='sigRole'>" + H(title) + "</div>");
            sb.AppendLine("<div class='sigRole'>Role: " + H(role) + "</div>");
            sb.AppendLine("<div class='sigRole'>Meaning: " + H(meaning) + "</div>");
            sb.AppendLine("</div>");
        }

        private static string GetPendingSignatureText(string title)
        {
            if (title.Equals(
                "Reviewed By",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Awaiting Technical Review";
            }

            if (title.Equals(
                "Approved By",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Awaiting QA Approval";
            }

            if (title.Equals(
                "Issued By",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Awaiting Issuance";
            }

            return "Pending";
        }

        private static void AddRow(
            StringBuilder sb,
            string a,
            string b,
            string c,
            string d)
        {
            sb.AppendLine(
                "<tr>" +
                "<th>" + H(a) + "</th>" +
                "<td>" + H(b) + "</td>" +
                "<th>" + H(c) + "</th>" +
                "<td>" + H(d) + "</td>" +
                "</tr>");
        }

        private static string H(string value)
        {
            return WebUtility.HtmlEncode(
                value ?? string.Empty);
        }

        private static string FormatDateOnly(DataRow row, string column)
        {
            if (row == null || !row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return string.Empty;

            return Convert.ToDateTime(row[column], CultureInfo.InvariantCulture)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string FirstAvailable(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred;
        }

        private static string FormatDateTime(
            DataRow row,
            string column)
        {
            if (row == null
                || !row.Table.Columns.Contains(column)
                || row[column] == DBNull.Value)
            {
                return string.Empty;
            }

            return Convert
                .ToDateTime(
                    row[column],
                    CultureInfo.InvariantCulture)
                .ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture);
        }

        private static string S(
            DataRow row,
            string column)
        {
            if (row == null
                || !row.Table.Columns.Contains(column)
                || row[column] == DBNull.Value)
            {
                return string.Empty;
            }

            return Convert.ToString(
                       row[column],
                       CultureInfo.InvariantCulture)
                   ?? string.Empty;
        }
    }
}
