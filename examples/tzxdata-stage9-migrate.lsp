;;; TZXDATA stage 9: objects for the VELEC_MIGRATE and VELEC_CHECK check in docs/autocad-verification.md.
;;; In a NEW drawing import examples/schema.json with VELEC_SCHEMA_IMPORT, load examples/stage3-selection.lsp and
;;; this file with APPLOAD, run XDATASTAGE3SAMPLE and VELECMIGRATESAMPLE, then VELEC_MIGRATE, VELEC_MIGRATE again
;;; (nothing to migrate) and VELEC_CHECK. The same objects are printed without AutoCAD by
;;; experiments/AcadPaletteSetXData.MigrationReport.

(defun velec:migrateblock ()
  ;; A block definition for the INSERT objects (template DEVICE accepts BlockReference).
  (if (not (tblsearch "BLOCK" "VELEC_DEVICE"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "VELEC_DEVICE") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 1.0)))
      (entmake '((0 . "ENDBLK"))))))

(defun velec:migrateinsert (x xdata)
  (entmakex
    (list '(0 . "INSERT") '(2 . "VELEC_DEVICE") (list 10 x 20.0 0.0)
      (list -3 (cons "BobrovXDATA" (append '((1070 . 2) (1000 . "TEMPLATE") (1000 . "DEVICE")) xdata))))))

(defun c:VELECMIGRATESAMPLE ()
  (regapp "SMARTLINE")
  (regapp "BobrovXDATA")
  (velec:migrateblock)
  ;; S: strings of the earlier RegApp SMARTLINE with Russian keys and one material.
  (entmakex
    '((0 . "LINE") (10 0.0 10.0 0.0) (11 10.0 10.0 0.0)
      (-3 ("SMARTLINE"
        (1000 . "Тип=Щит") (1000 . "Название=ЩР-1") (1000 . "Номер=7")
        (1000 . "Материал") (1002 . "{") (1000 . "Категория=Кабель")
        (1000 . "Марка=ВВГ 3x2,5") (1000 . "Количество=12") (1002 . "}")))))
  ;; D1, D2: a copied block keeps the DEVICE_ID; the smaller handle (D1, created first) keeps it in VELEC_CHECK.
  (velec:migrateinsert 0.0 '((1000 . "DEVICE_ID") (1000 . "{0B1C2D3E-0000-4000-8000-000000000001}") (1000 . "NMO_Name") (1000 . "ЩР-2")))
  (velec:migrateinsert 5.0 '((1000 . "DEVICE_ID") (1000 . "{0B1C2D3E-0000-4000-8000-000000000001}") (1000 . "NMO_Name") (1000 . "ЩР-2")))
  ;; D3: Old_Field is not described in the schema; add "rename": { "Old_Field": "Device_Class" } to migrate it.
  (velec:migrateinsert 10.0 '((1000 . "DEVICE_ID") (1000 . "{0B1C2D3E-0000-4000-8000-000000000002}")
    (1000 . "Old_Field") (1000 . "x") (1000 . "Device_Type") (1000 . "Щит")))
  (sssetfirst nil nil)
  (princ "\nСозданы линия SMARTLINE и блоки D1, D2 (общий DEVICE_ID), D3 (Old_Field). Выбор снят: VELEC_MIGRATE и VELEC_CHECK читают весь чертёж.")
  (princ))

(princ "\nVELECMIGRATESAMPLE — создать объекты для VELEC_MIGRATE и VELEC_CHECK.")
(princ)
