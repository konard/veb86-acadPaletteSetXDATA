;;; Issue #66: objects for the «Навигатор» tab (docs/autocad-verification.md, docs/navigator.md).
;;; Import examples/navigator-schema.json with VELEC_SCHEMA_IMPORT, copy examples/navigator/ to
;;; %APPDATA%\AcadPaletteSetXData\navigator\, load this file with APPLOAD and run VELECNAVSAMPLE.
;;; The same six objects are used by NavigatorTests and the WPF smoke test: two ЩР with the suffix 2
;;; (an automatic group), an object without NMO_BaseName («Не задано») and Count = 0 (PowerPerUnit fails).
;;; VELECNAVTOUCH changes the suffix of the first ЩР outside the palette; VELECNAVPLAIN adds an object
;;; without BobrovXDATA, which the navigator does not show.

(defun velec:navline (y data)
  (entmakex
    (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0)
      (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) data))))))

(defun c:VELECNAVSAMPLE ()
  (regapp "BobrovXDATA")
  (setq velec:nav-first
    (velec:navline 0.0 '((1000 . "NMO_BaseName") (1000 . "ЩР") (1000 . "NMO_Suffix") (1000 . "1")
                         (1000 . "Power") (1040 . 4.5) (1000 . "Phase") (1000 . "A") (1000 . "DB_link") (1000 . "db-01"))))
  (velec:navline 2.0 '((1000 . "NMO_BaseName") (1000 . "ЩР") (1000 . "NMO_Suffix") (1000 . "2")
                       (1000 . "Power") (1040 . 2.0) (1000 . "Phase") (1000 . "B")))
  (velec:navline 4.0 '((1000 . "NMO_BaseName") (1000 . "ЩР") (1000 . "NMO_Suffix") (1000 . "2")
                       (1000 . "Power") (1040 . 1.0) (1000 . "Phase") (1000 . "C")))
  (velec:navline 6.0 '((1000 . "NMO_BaseName") (1000 . "ВРУ") (1000 . "NMO_Suffix") (1000 . "1")
                       (1000 . "Power") (1040 . 12.5) (1000 . "Phase") (1000 . "A")))
  (velec:navline 8.0 '((1000 . "Power") (1040 . 0.5) (1000 . "Count") (1071 . 0)))
  (velec:navline 10.0 '((1000 . "NMO_BaseName") (1000 . "Лампа") (1000 . "Power") (1040 . 0.1)
                        (1000 . "Count") (1071 . 2) (1000 . "Phase") (1000 . "B")))
  (command "_.ZOOM" "_E")
  (princ "\nСоздано 6 объектов BobrovXDATA. Откройте вкладку «Навигатор» панели XDATAPALETTE.")
  (princ))

(defun c:VELECNAVTOUCH (/ data)
  (if (and velec:nav-first (entget velec:nav-first))
    (progn
      (setq data (entget velec:nav-first))
      (entmod (append data
        '((-3 ("BobrovXDATA" (1070 . 2) (1000 . "NMO_BaseName") (1000 . "ЩР") (1000 . "NMO_Suffix") (1000 . "7")
                             (1000 . "Power") (1040 . 4.5) (1000 . "Phase") (1000 . "A") (1000 . "DB_link") (1000 . "db-01"))))))
      (princ "\nСуффикс первого ЩР изменён на 7."))
    (princ "\nСначала выполните VELECNAVSAMPLE."))
  (princ))

(defun c:VELECNAVPLAIN ()
  (entmakex '((0 . "CIRCLE") (10 20.0 5.0 0.0) (40 . 2.0)))
  (princ "\nДобавлена окружность без BobrovXDATA.")
  (princ))

(princ "\nVELECNAVSAMPLE / VELECNAVTOUCH / VELECNAVPLAIN")
(princ)
