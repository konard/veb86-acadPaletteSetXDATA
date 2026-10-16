;;; TZXDATA stage 6: editing fixture for docs/autocad-verification.md (VST editing, Undo/Redo).
;;; Import examples/schema.json with VELEC_SCHEMA_IMPORT first, load this file with APPLOAD,
;;; run VELECEDITSAMPLE, edit the values in the palette and print the XDATA with VELECEDITXDATA
;;; after every edit, _U and _REDO.

(defun velec:editline (y data)
  (entmakex
    (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0)
      (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) data))))))

(defun velec:editrow (category mark count inspec)
  (list '(1002 . "{")
    '(1000 . "Mat_Category") (cons 1000 category)
    '(1000 . "Mat_Mark") (cons 1000 mark)
    '(1000 . "Mat_Count") (cons 1040 count)
    '(1000 . "Mat_InSpec") (cons 1070 inspec)
    '(1002 . "}")))

(defun c:VELECEDITSAMPLE (/ a b selection)
  (regapp "BobrovXDATA")
  (regapp "OtherApp")
  ;; A and B: template CABLE, different marks and groups (GC_Name = ЩУ1.3 / ЩУ1.4), the same
  ;; materials except the Mat_InSpec flag of СВ110-5.
  (setq a (velec:editline 0.0
    (append
      '((1000 . "TEMPLATE") (1000 . "CABLE")
        (1000 . "Cable_Mark") (1000 . "ВВГнг 3x2.5")
        (1000 . "Cable_Length") (1040 . 12.5)
        (1000 . "GC_HeadDevice") (1000 . "ЩУ1")
        (1000 . "GC_HDGroup") (1000 . "3")
        (1000 . "Materials") (1002 . "{"))
      (velec:editrow "LineFittings" "CD35" 2.0 1)
      (velec:editrow "Concrete" "СВ110-5" 1.0 1)
      '((1002 . "}")))))
  ;; Another RegApp after BobrovXDATA must survive every write.
  (entmod (append (entget a) '((-3 ("OtherApp" (1000 . "keep") (1071 . 42))))))
  (setq b (velec:editline 5.0
    (append
      '((1000 . "TEMPLATE") (1000 . "CABLE")
        (1000 . "Cable_Mark") (1000 . "ВВГнг 5x4")
        (1000 . "Cable_Length") (1040 . 8.0)
        (1000 . "GC_HeadDevice") (1000 . "ЩУ1")
        (1000 . "GC_HDGroup") (1000 . "4")
        (1000 . "Materials") (1002 . "{"))
      (velec:editrow "LineFittings" "CD35" 2.0 1)
      (velec:editrow "Concrete" "СВ110-5" 1.0 0)
      '((1002 . "}")))))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd a))
  (ssadd b selection)
  (sssetfirst nil selection)
  (princ "\nДва объекта выбраны: A (ВВГнг 3x2.5, ЩУ1.3) и B (ВВГнг 5x4, ЩУ1.4), шаблон CABLE.")
  (princ))

(defun c:VELECEDITXDATA (/ selection index item)
  (if (setq selection (ssget "_I"))
    (progn
      (setq index 0)
      (while (< index (sslength selection))
        (setq item (ssname selection index))
        (princ (strcat "\n" (cdr (assoc 5 (entget item))) ": "))
        (prin1 (cdr (assoc -3 (entget item '("*")))))
        (setq index (1+ index))))
    (princ "\nВыберите объекты перед командой."))
  (princ))

(princ "\nVELECEDITSAMPLE — создать два объекта для редактирования; VELECEDITXDATA — вывести XDATA выбранных объектов.")
(princ)
