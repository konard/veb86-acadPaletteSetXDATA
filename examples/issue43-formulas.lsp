;;; Issue #43: the formula mode of CalculatedString (format = "=…", NCalc). Fixture for a manual check in AutoCAD:
;;; import examples/issue43-formula-schema.json with VELEC_SCHEMA_IMPORT, load this file with APPLOAD,
;;; run VELECFORMULASAMPLE and look at the group «Формулы» of the palette (ru-RU decimal comma):
;;;   A: Обозначение ЩУ1.3, Подпись «ЩУ1.3 — Освещение», Расчётная мощность 26, «P=26 кВт», Нагрузка «большая»,
;;;      Длина, м 12,35, Подъём, мм 6172,5, Текст с = «=1».
;;;   B: Расчётная мощность 5, Нагрузка «отключено»; both selected — «Разное» where they differ.
;;; Change «Мощность» of A to 2 in the palette: only «Расчётная мощность», «Мощность, текст» and «Нагрузка» change,
;;; and the persisted «Расчётная мощность» is written to XDATA in the same transaction (VELECFORMULAXDATA prints it).

(defun velec:formulaline (y pairs)
  (entmakex
    (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0)
      (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) pairs))))))

(defun c:VELECFORMULASAMPLE (/ a b selection)
  (regapp "BobrovXDATA")
  (setq a (velec:formulaline 0.0
            '((1000 . "TEMPLATE") (1000 . "PANEL") (1000 . "Number") (1000 . "1") (1000 . "Group") (1000 . "3")
              (1000 . "Name") (1000 . "Освещение") (1000 . "Power") (1040 . 12.5) (1000 . "Reserve") (1040 . 0.5)
              (1000 . "Count") (1071 . 2) (1000 . "Length") (1040 . 12345.0) (1000 . "Angle") (1040 . 30.0))))
  (setq b (velec:formulaline 5.0
            '((1000 . "TEMPLATE") (1000 . "PANEL") (1000 . "Number") (1000 . "2") (1000 . "Group") (1000 . "1")
              (1000 . "Name") (1000 . "Розетки") (1000 . "Power") (1040 . 4.5) (1000 . "Reserve") (1040 . 0.5)
              (1000 . "Enabled") (1070 . 0) (1000 . "Length") (1040 . 1000.0))))
  (setq velec:formulaa a)
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd a))
  (ssadd b selection)
  (sssetfirst nil selection)
  (princ "\nДва щита выбраны: A (ЩУ1.3, 12,5 + 0,5 кВт × 2) и B (ЩУ2.1, отключён).")
  (princ))

(defun c:VELECFORMULAXDATA ()
  (if (and velec:formulaa (entget velec:formulaa))
    (print (assoc -3 (entget velec:formulaa '("BobrovXDATA"))))
    (princ "\nСначала выполните VELECFORMULASAMPLE."))
  (princ))

(princ "\nVELECFORMULASAMPLE — создать два щита с формулами; VELECFORMULAXDATA — показать XDATA щита A.")
(princ)
