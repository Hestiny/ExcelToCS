using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NPOI.SS.Util;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Norlo.ExcelToCS
{
    public class ExportExcelEditor : EditorWindow
    {
        [MenuItem("Tools/ExcelToCS/配置表规则 ", priority = -200)]
        private static void OpenInfo()
        {
            var win = GetWindow<ExportExcelEditor>();
            win.name = "导出规则";
            win.minSize = new Vector2(400, 500);
            win.Show();
        }

        protected void OnGUI()
        {
            GUILayout.BeginVertical();
            GUILayout.TextArea("目前支持多维数组和交错数组 枚举和结构体 结构体不支持数组\n\n" +
                               "枚举可以不限制在表格中声明的 但是需要再同一个命名空间下\n" +
                               "结构体必须得在表格中声明 否则将识别为枚举 因为结构体比枚举多需要一个构造方法\n" +
                               "需要饥饿加载的表  创建或者打开 TableCfg表格 从第1列和第2行开始往下面写需要饥饿加载表的类名就行\n\n" +
                               "需要用表格创建的枚举  创建或者打开 TableEnum表格 选择一列合并单元格并将值设为枚举名 该列后一列为枚举名 接着为枚举值 接着为注释(必须为两枚举值以上才创建)\n"
            );
            GUILayout.EndVertical();
        }
    }

    public class ExcelToCS
    {
        private const string _tableCfgTemplateFilePackagePath = "Packages/com.norlo.exceltocs/Editor/Export/Template/TableCfgTemplate.txt"; //配置表管理类
        private const string _configClassTemplateFileAssetPath = "Packages/com.norlo.exceltocs/Editor/Export/Template/ConfigClassTemplate.txt"; //程序具体调用的配置类
        private const string _configDefineClassTemplateFileAssetPath = "Packages/com.norlo.exceltocs/Editor/Export/Template/ConfigDefineClassTemplate.txt"; //每行数据定义的类
        private const string _configDataTemplateFileAssetPath = "Packages/com.norlo.exceltocs/Editor/Export/Template/ConfigDataTemplate.txt"; //初始化数据到配置类的类
        private const string _skipFieldMarkPrefix = "#";
        private static readonly string _defaultExcelDirFullPath = Application.dataPath.Replace("Assets", "Excel");
        private static readonly string _defaultExportDirPath = Application.dataPath + "/Script/Data/";

        private const int _divisionCount = 1000; //一个表的最大数据量限制

        /// <summary>
        /// 打开Excel文件夹
        /// </summary>
        public static void OpenExcelsFolder(string excelDirFullPath = null)
        {
            Process.Start("open", string.IsNullOrEmpty(excelDirFullPath) ? _defaultExcelDirFullPath : excelDirFullPath);
        }

        /// <summary>
        /// 导出Excel到cs
        /// </summary>
        /// <param name="excelFullPath">Excel存放的文件目录</param>
        /// <param name="csDirFullPath">cs代码导出的目录</param>
        public static void ExportExcels(string excelFullPath = null, string csDirFullPath = null)
        {
            ExcelToCS excelToCs = new ExcelToCS();
            string[] tablePaths = Directory.GetFiles(string.IsNullOrEmpty(excelFullPath) ? _defaultExcelDirFullPath : excelFullPath, "*", SearchOption.TopDirectoryOnly);
            string outDirFullPath = string.IsNullOrEmpty(csDirFullPath) ? _defaultExportDirPath : csDirFullPath;
            if (!Directory.Exists(outDirFullPath))
            {
                Directory.CreateDirectory(outDirFullPath);
            }
            excelToCs.ExportExcelInFilePath(tablePaths, string.IsNullOrEmpty(csDirFullPath) ? _defaultExportDirPath : csDirFullPath);
        }

        /// <summary>
        /// 挑选出excel文件并导出
        /// </summary>
        /// <param name="tableFilePaths"></param>
        /// <param name="exportCsDirPath"></param>
        /// <param name="isExportTableCfg"></param>
        public void ExportExcelInFilePath(IEnumerable<string> tableFilePaths, string exportCsDirPath, bool isExportTableCfg = true)
        {
            foreach (string fullPath in tableFilePaths)
            {
                if (fullPath.Contains("TableCfg"))
                {
                    Debug.Log($"导出主表: {Path.GetFileName(fullPath)}");
                    AddTableCfgD(fullPath);
                }
                else if (fullPath.Contains("TableEnum"))
                {
                    Debug.Log($"导出枚举: {Path.GetFileName(fullPath)}");
                    CreateTableEnumFile(fullPath, exportCsDirPath);
                }
                else if (fullPath.Contains("TableStruct"))
                {
                    Debug.Log($"导出结构体: {Path.GetFileName(fullPath)}");
                    CreateTableStructFile(fullPath, exportCsDirPath);
                }
                else if (fullPath.EndsWith(".xls") || fullPath.EndsWith(".xlsx"))
                {
                    Debug.Log($"导出配置: {Path.GetFileName(fullPath)}");
                    ExportExcelFile(fullPath, exportCsDirPath);
                }
            }

            if (isExportTableCfg)
            {
                CreateTableCfg(exportCsDirPath);
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"<color=green>====配置表导出完成====</color>");
        }

        /// <summary>
        /// 提取单个excel文件里信息
        /// </summary>
        /// <param name="fileFullPath"></param>
        /// <param name="exportCSDirPath"></param>
        public void ExportExcelFile(string fileFullPath, string exportCSDirPath)
        {
            Dictionary<string, List<List<object>>> excelInfoMap = new Dictionary<string, List<List<object>>>();
            // 处理每个表
            using (FileStream stream = new FileStream(fileFullPath, FileMode.Open, FileAccess.Read))
            {
                IWorkbook workbook = null;
                if (fileFullPath.EndsWith(".xlsx"))
                    workbook = new XSSFWorkbook(fileFullPath); //2007
                else if (fileFullPath.EndsWith(".xls"))
                    workbook = new HSSFWorkbook(stream); //2003

                // 处理每个sheet
                if (workbook != null)
                {
                    int sheetNumber = workbook.NumberOfSheets;
                    for (int sheetIndex = 0; sheetIndex < sheetNumber; sheetIndex++)
                    {
                        string sheetName = workbook.GetSheetName(sheetIndex);
                        //表名是以#开头，则跳过
                        if (sheetName.StartsWith(_skipFieldMarkPrefix) || sheetName.StartsWith("Sheet"))
                            continue;

                        List<List<object>> pickInfoList = new List<List<object>>();
                        ISheet sheet = workbook.GetSheetAt(sheetIndex);
                        sheet.ForceFormulaRecalculation = true; //强制公式计算

                        int maxColumnNum = sheet.GetRow(0).LastCellNum;
                        // 处理每行
                        for (int rowId = 0; rowId <= sheet.LastRowNum; rowId++)
                        {
                            List<object> rowInfoList = new List<object>();
                            IRow sheetRowInfo = sheet.GetRow(rowId);
                            if (sheetRowInfo == null)
                            {
                                Debug.LogErrorFormat("无法获取行数据 sheetName={0} ;rowId={1};rowMax={2}", sheetName, rowId, sheet.LastRowNum);
                            }

                            if (sheetRowInfo != null)
                            {
                                var rowFirstCell = sheetRowInfo.GetCell(0);
                                //跳过空行
                                if (null == rowFirstCell)
                                    continue;
                                if (rowFirstCell.CellType == CellType.Blank || rowFirstCell.CellType == CellType.Unknown ||
                                    rowFirstCell.CellType == CellType.Error)
                                    continue;
                            }

                            for (int columnId = 0; columnId < maxColumnNum; columnId++)
                            {
                                if (sheetRowInfo != null)
                                {
                                    ICell pickCell = sheetRowInfo.GetCell(columnId);

                                    if (pickCell is { IsMergedCell: true })
                                    {
                                        pickCell = GetMergeCell(sheet, pickCell.RowIndex, pickCell.ColumnIndex);
                                    }
                                    else if (pickCell == null)
                                    {
                                        // 有时候合并的格子索引为空,就直接通过索引去找合并的格子
                                        pickCell = GetMergeCell(sheet, rowId, columnId);
                                    }

                                    //公式结果
                                    if (pickCell is { CellType: CellType.Formula })
                                    {
                                        pickCell.SetCellType(CellType.String);
                                        rowInfoList.Add(pickCell.StringCellValue);
                                    }
                                    else if (pickCell != null)
                                    {
                                        rowInfoList.Add(pickCell.ToString());
                                    }
                                    else
                                    {
                                        rowInfoList.Add("");
                                    }
                                }
                            }

                            pickInfoList.Add(rowInfoList);
                        }

                        excelInfoMap.Add(sheetName, pickInfoList);
                    }
                }
            }

            foreach (var item in excelInfoMap)
            {
                ParseExcelToCS(item.Value, item.Key, item.Key, exportCSDirPath);
            }
        }

        #region ====TableCfg主表创建====

        private readonly StringBuilder _tableCfgV = new StringBuilder();
        private readonly StringBuilder _tableCfgD = new StringBuilder();

        private const string _tableCfgFormat = @"
        private [CONFIGNAME] _[CONFIGNAMEName];

        public [CONFIGNAME] [CONFIGNAMEName]
        {
            get { return _[CONFIGNAMEName] ??= new [CONFIGNAME](); }
            set => _[CONFIGNAMEName] = value;
        }
        ";

        private void AddTableCfgD(string fullPath)
        {
            _tableCfgV.Clear();
            _tableCfgD.Clear();
            using FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
            IWorkbook workbook = null;
            if (fullPath.EndsWith(".xlsx"))
                workbook = new XSSFWorkbook(fullPath); //2007
            else if (fullPath.EndsWith(".xls"))
                workbook = new HSSFWorkbook(stream); //2003

            // 处理每个sheet
            if (workbook == null) return;

            int sheetNumber = workbook.NumberOfSheets;
            for (int sheetIndex = 0; sheetIndex < sheetNumber; sheetIndex++)
            {
                string sheetName = workbook.GetSheetName(sheetIndex);
                //表名是以#开头，则跳过
                if (sheetName.StartsWith(_skipFieldMarkPrefix))
                    continue;

                ISheet sheet = workbook.GetSheetAt(sheetIndex);
                sheet.ForceFormulaRecalculation = true; //强制公式计算

                // 处理每行
                for (int rowId = 1; rowId <= sheet.LastRowNum; rowId++)
                {
                    IRow sheetRowInfo = sheet.GetRow(rowId);
                    if (sheetRowInfo == null)
                    {
                        Debug.LogErrorFormat("无法获取行数据 sheetName={0} ;rowId={1};rowMax={2}", sheetName, rowId, sheet.LastRowNum);
                        continue;
                    }

                    var rowFirstCell = sheetRowInfo.GetCell(0);
                    //跳过空行
                    if (null == rowFirstCell)
                        continue;
                    if (rowFirstCell.CellType is CellType.Blank or CellType.Unknown or CellType.Error)
                        continue;

                    rowFirstCell.SetCellType(CellType.String);
                    _tableCfgD.AppendFormat("\n           _{0} = new {1}();", ReplaceTableCfgName(rowFirstCell.ToString()), rowFirstCell);
                }
            }
        }

        private void AddTableCfgV(string configName)
        {
            return;
            // StringBuilder sb = new StringBuilder(_tableCfgFormat);
            // sb.Replace("[CONFIGNAME]", configName);
            // sb.Replace("[CONFIGNAMEName]", ReplaceTableCfgName(configName));
            // _tableCfgV.Append(sb);
        }

        private void CreateTableCfg(string exportCSDirPath)
        {
            TextAsset tableCfgFormat = (TextAsset)AssetDatabase.LoadAssetAtPath(_tableCfgTemplateFilePackagePath, typeof(TextAsset));
            StringBuilder tableCfgFormatContent = new StringBuilder(tableCfgFormat.text);
            tableCfgFormatContent.Replace("[TABLECFGV]", _tableCfgV.ToString());
            tableCfgFormatContent.Replace("[TABLECFGD]", _tableCfgD.ToString());
            using StreamWriter sw = new StreamWriter(File.Create(exportCSDirPath + "TableCfg.cs"));
            sw.Write(tableCfgFormatContent.ToString());
        }

        //首字母改成小写 去掉Config
        private static string ReplaceTableCfgName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 1)
            {
                return default;
            }

            StringBuilder sb = new StringBuilder(char.ToLower(name[0]).ToString());
            sb.Append(name.Substring(1, name.Length - 1));
            sb.Replace("Config", "");
            return sb.ToString();
        }

        #endregion

        #region ====TableStruct====

        private readonly List<string> _curTableStruct = new List<string>();

        private const string _tableStructFormat = @"
namespace TableDataConfig
{
[TableStruct]
}
";

        private void CreateTableStructFile(string fullPath, string exportCSDirPath)
        {
            _curTableStruct.Clear();
            StringBuilder structContent = new StringBuilder();
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read))
            {
                IWorkbook workbook = null;
                if (fullPath.EndsWith(".xlsx"))
                    workbook = new XSSFWorkbook(fullPath); //2007
                else if (fullPath.EndsWith(".xls"))
                    workbook = new HSSFWorkbook(stream); //2003

                // 处理每个sheet
                if (workbook != null)
                {
                    ISheet sheet = workbook.GetSheetAt(0);
                    for (int i = 0; i < sheet.NumMergedRegions; i++)
                    {
                        structContent.Append(CreatTableStruct(sheet, sheet.GetMergedRegion(i)));
                    }
                }
            }

            StringBuilder sb = new StringBuilder(_tableStructFormat);
            sb.Replace("[TableStruct]", structContent.ToString());
            using (StreamWriter sw = new StreamWriter(File.Create(exportCSDirPath + "TableStruct.cs")))
                sw.Write(sb.ToString());
        }

        private string CreatTableStruct(ISheet sheet, CellRangeAddress merge)
        {
            if (merge == null)
            {
                return "";
            }

            int curCol = merge.FirstColumn;
            int rowFirst = merge.FirstRow;
            int rowLast = merge.LastRow;
            StringBuilder sb = new StringBuilder();
            var row = sheet.GetRow(rowFirst);
            var cell = row.GetCell(curCol);
            string structName = cell.ToString();
            _curTableStruct.Add(structName);

            sb.AppendFormat("\tpublic struct {0}\n", structName);
            sb.Append("\t{\n");
            for (int i = rowFirst; i <= rowLast; i++)
            {
                row = sheet.GetRow(i);
                cell = row.GetCell(curCol + 1);
                if (cell == null)
                {
                    continue;
                }

                string structTypeName = cell.ToString();
                string structValueName = row.GetCell(curCol + 2)?.ToString() ?? "";
                string structRemark = row.GetCell(curCol + 3)?.ToString() ?? "";
                if (string.IsNullOrEmpty(structTypeName))
                {
                    continue;
                }

                sb.AppendFormat("\t\t///{2}\n\t\tpublic {0} {1};\n", structTypeName, structValueName, structRemark);
            }

            //增加构造方法
            sb.AppendFormat("\n\t\tpublic {0}(", structName);
            StringBuilder initClass = new StringBuilder();
            for (int i = rowFirst; i <= rowLast; i++)
            {
                row = sheet.GetRow(i);
                cell = row.GetCell(curCol + 1);
                if (cell == null)
                {
                    continue;
                }

                string structTypeName = cell.ToString();
                string structValueName = row.GetCell(curCol + 2)?.ToString() ?? "";
                if (string.IsNullOrEmpty(structTypeName))
                {
                    continue;
                }

                if (i != rowFirst)
                {
                    sb.Append(",");
                }

                sb.AppendFormat("{0} {1}", structTypeName, structValueName);
                initClass.AppendFormat("\n\t\t\tthis.{0} = {0};", structValueName);
            }

            sb.Append(")\n");
            sb.AppendFormat("\t\t{{{0}\n\t\t}}", initClass);

            sb.Append("\n\t}\n");

            return sb.ToString();
        }

        #endregion

        #region ====TableEnum创建====

        //枚举可以不限制在表格中声明的 但是需要再同一个命名空间下
        //存储当前创建的枚举值名称
        private readonly List<string> _curTableEnum = new List<string>();

        private const string _tableEnumFormat = @"
namespace TableDataConfig
{
[TableEnum]
}
";

        ///必须两个枚举起步 不然不创建
        private void CreateTableEnumFile(string fullPath, string exportCSDirPath)
        {
            _curTableEnum.Clear();
            StringBuilder enumContent = new StringBuilder();
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read))
            {
                IWorkbook workbook = null;
                if (fullPath.EndsWith(".xlsx"))
                    workbook = new XSSFWorkbook(fullPath); //2007
                else if (fullPath.EndsWith(".xls"))
                    workbook = new HSSFWorkbook(stream); //2003

                // 处理每个sheet
                if (workbook != null)
                {
                    ISheet sheet = workbook.GetSheetAt(0);
                    for (int i = 0; i < sheet.NumMergedRegions; i++)
                    {
                        enumContent.Append(CreatTableEnum(sheet, sheet.GetMergedRegion(i)));
                    }
                }
            }

            StringBuilder sb = new StringBuilder(_tableEnumFormat);
            sb.Replace("[TableEnum]", enumContent.ToString());
            using (StreamWriter sw = new StreamWriter(File.Create(exportCSDirPath + "TableEnum.cs")))
                sw.Write(sb.ToString());
        }

        private string CreatTableEnum(ISheet sheet, CellRangeAddress merge)
        {
            if (merge == null)
            {
                return "";
            }

            int curCol = merge.FirstColumn;
            int rowFirst = merge.FirstRow;
            int rowLast = merge.LastRow;
            StringBuilder sb = new StringBuilder();
            var row = sheet.GetRow(rowFirst);
            var cell = row.GetCell(curCol);
            string enumName = cell.ToString();
            _curTableEnum.Add(enumName);

            sb.AppendFormat("\t[Flags]\n\tpublic enum {0}\n", enumName);
            if (!enumName.Contains("[Flags]"))
            {
                sb.Replace("[Flags]", "");
            }

            sb.Append("\t{\n");
            for (int i = rowFirst; i <= rowLast; i++)
            {
                row = sheet.GetRow(i);
                cell = row.GetCell(curCol + 1);
                if (cell == null)
                {
                    continue;
                }

                string enumValueName = cell.ToString();
                string enumValue = row.GetCell(curCol + 2)?.ToString() ?? "";
                string enumRemark = row.GetCell(curCol + 3)?.ToString() ?? "";
                if (string.IsNullOrEmpty(enumValueName))
                {
                    continue;
                }

                sb.AppendFormat("\t\t///{2}\n\t\t{0} = {1},\n", enumValueName, enumValue, enumRemark);
            }

            sb.Append("\t}\n");

            return sb.ToString();
        }

        #endregion

        /// <summary>
        /// 获取合并的格子的原格子
        /// 合并格子的首行首列就是合并单元格的信息
        /// </summary>
        /// <param name="sheet"></param>
        /// <param name="rowIndex"></param>
        /// <param name="colIndex"></param>
        /// <returns></returns>
        private static ICell GetMergeCell(ISheet sheet, int rowIndex, int colIndex)
        {
            for (int ii = 0; ii < sheet.NumMergedRegions; ii++)
            {
                var cellRange = sheet.GetMergedRegion(ii);
                if (colIndex < cellRange.FirstColumn ||
                    colIndex > cellRange.LastColumn ||
                    rowIndex < cellRange.FirstRow ||
                    rowIndex > cellRange.LastRow)
                    continue;
                var row = sheet.GetRow(cellRange.FirstRow);
                var mergeCell = row.GetCell(cellRange.FirstColumn);

                return mergeCell;
            }

            return null;
        }

        private void ParseExcelToCS(List<List<object>> totalRowInfoList, string tableName, string saveFileName, string exportCSDirPath)
        {
            TextAsset formatContent = (TextAsset)AssetDatabase.LoadAssetAtPath(_configDefineClassTemplateFileAssetPath, typeof(TextAsset));
            TextAsset exSubContent = (TextAsset)AssetDatabase.LoadAssetAtPath(_configDataTemplateFileAssetPath, typeof(TextAsset));
            if (!exportCSDirPath.EndsWith("/"))
                exportCSDirPath += "/";
            string saveFullPath = exportCSDirPath + saveFileName + ".cs";
            if (File.Exists(saveFullPath))
                File.Delete(saveFullPath);

            PickTableClassFields(totalRowInfoList, tableName, out var fieldContent, out var dataContent, out var classParamContent, out var assignmentContent, out var firstFieldTypeContent);
            string convertTableName = tableName;
            StringBuilder saveContent = new StringBuilder(formatContent.text);
            //表名
            saveContent = saveContent.Replace("[CONFIGNAME]", convertTableName);
            //字段定义
            saveContent = saveContent.Replace("[FIELDNAMES]", fieldContent.ToString());
            //实例化时的字段名
            saveContent = saveContent.Replace("[CLASSPARAMS]", classParamContent.ToString());
            //赋值
            saveContent = saveContent.Replace("[ASSIGNMENT]", assignmentContent.ToString());

            int maxDataClassIndex = dataContent.Count;
            TextAsset configClassFormat = (TextAsset)AssetDatabase.LoadAssetAtPath(_configClassTemplateFileAssetPath, typeof(TextAsset));
            StringBuilder configClassContent = new StringBuilder(configClassFormat.text);
            configClassContent = configClassContent.Replace("[CONFIGNAME]", tableName);
            configClassContent = configClassContent.Replace("[CONFIGNAME_CONVERT]", convertTableName);
            configClassContent = configClassContent.Replace("[FIRSTFIELDTYPE]", firstFieldTypeContent.ToString());
            StringBuilder addListContent = new StringBuilder();
            for (int i = 0; i < maxDataClassIndex; i++)
                addListContent.AppendFormat("{0}{1}.data , ", tableName, i.ToString());
            configClassContent = configClassContent.Replace("[ADDLIST]", addListContent.ToString());

            saveContent.Append("\n\r");
            saveContent.Append(configClassContent.ToString());

            StringBuilder configDataContent = new StringBuilder();
            StringBuilder checkListV = new StringBuilder();
            StringBuilder checkListD = new StringBuilder();

            for (int i = 0; i < maxDataClassIndex; i++)
            {
                StringBuilder unitDataContent = new StringBuilder(exSubContent.text);
                unitDataContent = unitDataContent.Replace("[CONFIGNAME]", tableName);
                unitDataContent = unitDataContent.Replace("[CONFIGNAME_CONVERT]", convertTableName);
                unitDataContent = unitDataContent.Replace("[INDEX]", i.ToString());
                unitDataContent = unitDataContent.Replace("[FIRSTFIELDTYPE]", firstFieldTypeContent.ToString());
                //添加具体配置参数
                unitDataContent = unitDataContent.Replace("[CONFIG_DATA]", dataContent[i].ToString());
                configDataContent.Append("\n\r");
                configDataContent.Append(unitDataContent.ToString());
                AddCheckList(ref checkListV, ref checkListD, tableName, i);
            }

            saveContent.Replace("[checkListV]", checkListV.ToString());
            saveContent.Replace("[checkListD]", checkListD.ToString());
            saveContent.Replace("[CONFIGDATATEMPLATE]", configDataContent.ToString());

            AddTableCfgV(tableName);

            using StreamWriter sw = new StreamWriter(File.Create(saveFullPath));
            sw.Write(saveContent.ToString());
        }

        /// <summary>
        /// [CHECKLISTCLASS] = [CONFIGNAME][INDEX]
        /// </summary>
        private const string _checkListVTemplate = @"
        [CHECKLISTCLASS] [CHECKLISTCLASS] = new [CHECKLISTCLASS]();
";

        /// <summary>
        /// [CHECKLISTCLASS] = [CONFIGNAME][INDEX]
        /// </summary>
        private const string _checkListDTemplate = @"
        [CHECKLISTCLASS].GetData(),
";

        private void AddCheckList(ref StringBuilder checkListV, ref StringBuilder checkListD, string tableName, int index)
        {
            StringBuilder sbV = new StringBuilder(_checkListVTemplate);
            StringBuilder sbD = new StringBuilder(_checkListDTemplate);
            string checkListClass = tableName + index;
            sbV.Replace("[CHECKLISTCLASS]", checkListClass);
            sbD.Replace("[CHECKLISTCLASS]", checkListClass);
            checkListV.Append(sbV);
            checkListD.Append(sbD);
        }

        private void PickTableClassFields(List<List<object>> totalRowInfoList, string tableName, out StringBuilder fieldContent, out List<StringBuilder> dataContent, out StringBuilder classParamContent, out StringBuilder assignmentContent, out StringBuilder firstFieldTypeContent)
        {
            //第一行是备注，第二行是字段类型，第三行是字段名
            List<object> oneRowInfoList = totalRowInfoList[0];
            List<object> twoRowInfoList = totalRowInfoList[1];
            List<object> threeRowInfoList = totalRowInfoList[2];

            fieldContent = new StringBuilder();                                      //替换[FIELDNAMES]
            dataContent = new List<StringBuilder>();                                 //替换[ELEMENTS]
            classParamContent = new StringBuilder();                                 //替换[CLASSPARAMS]
            assignmentContent = new StringBuilder("");                               //替换[ASSIGNMENT]
            firstFieldTypeContent = new StringBuilder(twoRowInfoList[0].ToString()); //替换[FIRSTFIELDTYPE]

            StringBuilder unitDataFormatContent = new StringBuilder($"new {tableName}Class(");
            List<int> validIndexList = new List<int>();
            List<string> checkSameParamNameList = new List<string>();

            string prefixStr = "    public readonly";
            int maxIndex = oneRowInfoList.Count - 1;
            for (int i = 0; i <= maxIndex; i++)
            {
                //当字段说明是以#开头，则跳过该字段
                string remarkStr = oneRowInfoList[i].ToString();
                if (remarkStr.StartsWith(_skipFieldMarkPrefix))
                    continue;
                string fieldNameStr = threeRowInfoList[i].ToString();
                if (fieldNameStr.StartsWith(_skipFieldMarkPrefix))
                    continue;

                if (checkSameParamNameList.Contains(fieldNameStr))
                {
                    Debug.LogErrorFormat("{0}存在相同的参数名字{1}", tableName, fieldNameStr);
                    continue;
                }

                checkSameParamNameList.Add(fieldNameStr);

                string fieldTypeStr = twoRowInfoList[i].ToString();

                fieldContent.AppendFormat("    /// <summary>\r\n    /// {0}\r\n    /// </summary>\r\n", remarkStr.Replace("\n", "     "));
                fieldContent.AppendFormat("{0} {1} {2};\r\n", prefixStr, fieldTypeStr, fieldNameStr);

                if (classParamContent.Length > 0)
                {
                    classParamContent.Append(", ");
                    unitDataFormatContent.Append(", ");
                }

                classParamContent.AppendFormat("{0} {1}", fieldTypeStr, fieldNameStr);
                unitDataFormatContent.AppendFormat("[p{0}]", i.ToString());
                assignmentContent.AppendFormat("        this.{0} = {0};\r\n", fieldNameStr);

                validIndexList.Add(i);
            }

            //这里加上[]是为了防止部分重名参数
            unitDataFormatContent.Insert(0, "        {[p0] , ").Append(") }");

            int dataAppendIndex = 0;
            StringBuilder eachDataContent = new StringBuilder();
            //从第四行开始读取
            int rowInfoCount = totalRowInfoList.Count;
            for (int i = 3; i < rowInfoCount; i++)
            {
                var rowInfoList = totalRowInfoList[i];
                StringBuilder unitContent = new StringBuilder(unitDataFormatContent.ToString());
                foreach (var realTableColumn in validIndexList)
                {
                    object value = rowInfoList[realTableColumn];
                    string fieldTypeStr = twoRowInfoList[realTableColumn].ToString();
                    string convertStr = ConvertObjValue(fieldTypeStr, value);
                    if (convertStr.StartsWith("error"))
                    {
                        Debug.LogErrorFormat("配置表中值的格式错误。表名={0} ; 行={1} ; 列={2}", tableName, (i + 1).ToString(), (realTableColumn + 1).ToString());
                    }
                    else
                    {
                        string replaceTagStr = "[p" + realTableColumn.ToString() + "]";
                        unitContent = unitContent.Replace(replaceTagStr, convertStr);
                    }
                }

                eachDataContent.Append(unitContent);
                eachDataContent.Append(",\n");
                dataAppendIndex++;
                //超过1000行另外新建一个表存储
                if (dataAppendIndex >= _divisionCount)
                {
                    dataAppendIndex = 0;
                    dataContent.Add(eachDataContent);
                    eachDataContent = new StringBuilder();
                }
                else if (i == rowInfoCount - 1)
                {
                    dataAppendIndex = 0;
                    dataContent.Add(eachDataContent);
                    eachDataContent = new StringBuilder();
                }
            }
        }

        #region 转换声明

        /// <summary>
        /// 将字符转换为值初始化语句
        /// 字符串结构 类似{{***,***},{***}}
        /// 转换为
        /// 加后缀 {{***f,***f},{***f}}
        /// 加new { new []{***f,***f},new [] {***f}}
        /// </summary>
        /// <param name="str">{{***,***},{***}}</param>
        /// <param name="type">int[][]</param>
        /// <param name="isHaveType"> 是否包含类型声明 是new int[] 否 new []</param>
        private string ConvertArray(string str, string type, bool isHaveType = true)
        {
            StringBuilder curStr = new StringBuilder(str);
            curStr.Replace(" ", "");             //移除空白字符
            bool isJagged = !type.Contains(","); //为交错数组
            //当前的数组总层级 最小值为1 否则就是传入参数不规范
            int curLevel = 0;
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] != '{')
                {
                    curLevel = i;
                    if (i == 0)
                    {
                        Debug.LogError("传入参数不规范 少了一层\"{}\"");
                    }

                    break;
                }
            }

            StringBuilder sb = new StringBuilder();
            if (isJagged)
            {
                //增加前缀
                if (isHaveType)
                {
                    sb.AppendFormat("new {0}", type);
                    sb.Append("{");
                }
                else
                {
                    sb.Append("new []{");
                }
            }
            else
            {
                sb.AppendFormat("new {0}", type);
                sb.Append("{");
            }

            //增加子项
            //移除curStr第一个字符和最后一个字符的大括号
            curStr.Remove(0, 1);
            curStr.Remove(curStr.Length - 1, 1);
            //替换当前层子项分隔标识符
            StringBuilder subLevelTag = new StringBuilder();
            StringBuilder subSplitTag = new StringBuilder();
            for (int i = 0; i < curLevel - 1; i++)
            {
                subLevelTag.Append("}");
            }

            subSplitTag.Append(subLevelTag);
            subSplitTag.Append(",");

            curStr.Replace(subSplitTag.ToString(), subLevelTag.Append("*").ToString());
            //子项
            var sbList = curStr.ToString().Split("*");
            if (sbList != null)
            {
                for (int j = 0; j < sbList.Length; j++)
                {
                    var curSubStr = sbList[j];
                    if (j != 0)
                    {
                        sb.Append(",");
                    }

                    //最后一层转换值
                    sb.Append(curLevel == 1 ? ConvertValue(curSubStr, type) : ConvertSubArray(curSubStr, type, isHaveType));
                }
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 子项迭代 解决多维数组问题
        /// </summary>
        /// <param name="str"></param>
        /// <param name="type"></param>
        /// <param name="isHaveType"></param>
        /// <returns></returns>
        private string ConvertSubArray(string str, string type, bool isHaveType = true)
        {
            StringBuilder curStr = new StringBuilder(str);
            curStr.Replace(" ", "");             //移除空白字符
            bool isJagged = !type.Contains(","); //为交错数组
            //当前的数组总层级 最小值为1 否则就是传入参数不规范
            int curLevel = 0;
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] != '{')
                {
                    curLevel = i;
                    if (i == 0)
                    {
                        Debug.LogError("传入参数不规范 少了一层\"{}\"");
                    }

                    break;
                }
            }

            StringBuilder sb = new StringBuilder();
            if (isJagged)
            {
                //增加前缀
                if (isHaveType)
                {
                    sb.AppendFormat("new {0}", GetTypeName(type));
                    for (int i = 0; i < curLevel; i++)
                    {
                        sb.Append("[]");
                    }

                    sb.Append("{");
                }
                else
                {
                    sb.Append("new []{");
                }
            }
            else
            {
                sb.Append("{");
            }


            //增加子项
            //移除curStr第一个字符和最后一个字符的大括号
            curStr.Remove(0, 1);
            curStr.Remove(curStr.Length - 1, 1);
            //替换当前层子项分隔标识符
            StringBuilder subLevelTag = new StringBuilder();
            StringBuilder subSplitTag = new StringBuilder();
            for (int i = 0; i < curLevel - 1; i++)
            {
                subLevelTag.Append("}");
            }

            subSplitTag.Append(subLevelTag);
            subSplitTag.Append(",");

            curStr.Replace(subSplitTag.ToString(), subLevelTag.Append("*").ToString());
            //子项
            var sbList = curStr.ToString().Split("*");
            if (sbList != null)
            {
                for (int j = 0; j < sbList.Length; j++)
                {
                    var curSubStr = sbList[j];
                    if (j != 0)
                    {
                        sb.Append(",");
                    }

                    //最后一层转换值
                    sb.Append(curLevel == 1 ? ConvertValue(curSubStr, type) : ConvertSubArray(curSubStr, type, isHaveType));
                }
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 变量类型 和变量后缀声明
        /// </summary>
        private static readonly Dictionary<string, string> _normalTypeList = new Dictionary<string, string>
        {
            { "short", "" },
            { "ushort", "" },
            { "string", "" },
            { "int", "" },
            { "uint", "" },
            { "float", "f" },
            { "bool", "" },
            { "long", "" },
            { "ulong", "" },
            { "double", "" },
            { "char", "" },
            { "Vector3", "f" },
            { "Vector2", "f" },
            { "DateTime", "" },
        };

        /// 是否为常规类型 (不需要类型名限定)
        private static bool IsNormalType(string fieldType)
        {
            StringBuilder sb = new StringBuilder(fieldType);
            sb.Replace("[", "");
            sb.Replace("]", "");
            sb.Replace(",", "");
            return _normalTypeList.ContainsKey(sb.ToString());
        }

        private static string GetTypeName(string filedType)
        {
            StringBuilder sb = new StringBuilder(filedType);
            sb.Replace("[", "");
            sb.Replace("]", "");
            sb.Replace(",", "");
            return sb.ToString();
        }

        private string ConvertObjValue(string firstFieldType, object value)
        {
            if (value == null)
            {
                return GetTypeName(firstFieldType) == "string" ? "\"\"" : "default";
            }

            //数组
            if (firstFieldType.Contains("["))
            {
                string valueStr = (string)value;
                return ConvertArray($"{{{valueStr}}}", firstFieldType);
            }
            else if (_curTableStruct.Contains(firstFieldType))
            {
                return $"new {GetTypeName(firstFieldType)} ({value})";
            }
            //单个声明
            else
            {
                return ConvertValue((string)value, firstFieldType);
            }
        }

        private string ConvertValue(string value, string firstType)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (firstType.Contains("["))
                {
                    return "";
                }
                else if (GetTypeName(firstType) == "string")
                {
                    return "\"\"";
                }

                return "default";
            }

            string type = GetTypeName(firstType);
            //特殊处理char和string 需要加" '
            string endStr = _normalTypeList.TryGetValue(type, out var value1) ? value1 : "";
            StringBuilder sb = new StringBuilder();
            StringBuilder sbValue = new StringBuilder(value);
            if (type == "string")
            {
                return sb.AppendFormat("\"{0}\"", sbValue).ToString();
            }
            else if (type == "char")
            {
                return sb.AppendFormat("\'{0}\'", sbValue).ToString();
            }

            //处理每个值 如果是枚举需要加限定的类型
            if (IsNormalType(type))
            {
                return sbValue.Append(endStr).ToString();
            }
            else
            {
                //TODO: 后续考虑如果用数值的话直接转成枚举值
                return sbValue.Insert(0, type + ".").ToString();
            }
        }

        //配置表中格式(1,2.0,3.0)
        static string ConvertValueToVector3(object value)
        {
            // string floatRegexStr = @"(-?\d+\.?\d*)";
            string vector3RegexStr = @"^\((-?\d+\.?\d*),(-?\d+\.?\d*),(-?\d+\.?\d*)\)$";
            Regex regex = new Regex(vector3RegexStr);
            string content = value.ToString().Replace(" ", "");
            if (regex.IsMatch(content))
            {
                string result = regex.Replace(content, "new Vector3($1f,$2f,$3f)");
                return result;
            }
            else
                return "error: Vector3类型错误";
        }

        #endregion
    }
}