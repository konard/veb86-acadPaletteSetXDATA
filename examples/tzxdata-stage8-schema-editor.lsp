;;; TZXDATA stage 8: objects for the schema editor check in docs/autocad-verification.md.
;;; Import examples/schema.json with VELEC_SCHEMA_IMPORT first, load this file and examples/tzxdata-stage2-nod.lsp
;;; with APPLOAD, run VELECSCHEMASAMPLE, open VELEC_SCHEMA_EDIT with the blocks selected and print NOD/VELEC with
;;; VELECDUMP after «Применить», _U and _REDO.

(defun velec:schemablock ()
  ;; A block definition for the INSERT objects (template DEVICE accepts BlockReference).
  (if (not (tblsearch "BLOCK" "VELEC_DEVICE"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "VELEC_DEVICE") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 1.0)))
      (entmake '((0 . "ENDBLK"))))))

(defun velec:schemainsert (x xdata)
  (entmakex
    (list '(0 . "INSERT") '(2 . "VELEC_DEVICE") (list 10 x 0.0 0.0)
      (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) xdata))))))

(defun c:VELECSCHEMASAMPLE (/ e1 e2 selection)
  (regapp "BobrovXDATA")
  (velec:schemablock)
  ;; E1: template DEVICE, the operands of the formula GC_Name (ЩУ1.3), Power and the enum key _AC_220V_50Hz.
  (setq e1 (velec:schemainsert 0.0
    '((1000 . "DEVICE_ID") (1000 . "6B29FC40-CA47-1067-B31D-00DD010662DA")
      (1000 . "TEMPLATE") (1000 . "DEVICE")
      (1000 . "GC_HeadDevice") (1000 . "ЩУ1")
      (1000 . "GC_HDGroup") (1000 . "3")
      (1000 . "Power") (1040 . 2.5)
      (1000 . "Voltage") (1000 . "_AC_220V_50Hz"))))
  ;; E2: the same enum key without a template: the key is stored in two objects.
  (setq e2 (velec:schemainsert 5.0 '((1000 . "Voltage") (1000 . "_AC_220V_50Hz"))))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd e1))
  (ssadd e2 selection)
  (sssetfirst nil selection)
  (princ "\nВыбраны блоки E1 (DEVICE, ЩУ1.3, Power = 2.5) и E2; у обоих Voltage = _AC_220V_50Hz.")
  (princ))

(princ "\nVELECSCHEMASAMPLE — создать и выбрать блоки E1, E2 для VELEC_SCHEMA_EDIT; VELECDUMP — вывести NOD/VELEC.")
(princ)
