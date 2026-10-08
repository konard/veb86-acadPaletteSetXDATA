; Load with APPLOAD in a NEW AutoCAD 2021 drawing; run XDATASTAGE3SAMPLE.
; This fixture creates two entities and sample data. Stages 3 and 4 use this fixture for inspection and group editing.
(defun c:XDATASTAGE3SAMPLE (/ line cable dictionary selection)
  (regapp "ESMT_LEP_v1.0")
  (regapp "BobrovXDATA")
  (setq line
    (entmakex
      '((0 . "LINE") (10 0.0 0.0 0.0) (11 10.0 0.0 0.0)
        (-3 ("ESMT_LEP_v1.0"
          (1000 . "Type=Superline") (1000 . "Number=021") (1000 . "Name=Line A")
          (1000 . "ProjectReference=Project 021")
          (1000 . "Material") (1002 . "{") (1000 . "Category=Steel")
          (1000 . "Name=CD35") (1000 . "Count=1,2") (1000 . "IsInSpec=1")
          (1000 . "Comment=Example") (1002 . "}"))))))
  (setq cable
    (entmakex
      '((0 . "LWPOLYLINE") (100 . "AcDbEntity") (100 . "AcDbPolyline")
        (90 . 3) (70 . 0) (10 0.0 5.0) (10 5.0 7.0) (10 10.0 5.0)
        (-3 ("BobrovXDATA" (1000 . "Number=022"))))))
  (setq dictionary (vlax-vla-object->ename (vla-GetExtensionDictionary (vlax-ename->vla-object cable))))
  (dictadd dictionary "BobrovXDATA"
    (entmakex
      '((0 . "XRECORD") (100 . "AcDbXrecord") (280 . 1)
        (1 . "<VisualTreeString><Properties><Type>Cable</Type><Name>Cable B</Name></Properties>")
        (1 . "<Materials><Material Category='Armature' Name='CD35' Count='2' IsInSpec='false'/></Materials></VisualTreeString>"))))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd line))
  (ssadd cable selection)
  (sssetfirst nil selection)
  (princ "\nTwo sample entities selected. Open XDATAPALETTE to compare/edit the group and inspect each object tree.")
  (princ))
(vl-load-com)
(princ)
