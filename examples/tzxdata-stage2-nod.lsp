;;; TZXDATA stage 2: prints the parameter schema stored in NOD/VELEC.
;;; Load with APPLOAD and run VELECDUMP before/after VELEC_SCHEMA_IMPORT, U, DXF save/reopen.
;;; Only the schema codes are printed: 1/2/303 field names, 300/302 text and JSON fragments,
;;; 40 real, 90 integer, 290 boolean.

(defun velec:dump (ename path / data key obj)
  (setq data (entget ename))
  (while data
    (cond
      ((= (caar data) 3) (setq key (cdar data)))
      ((and key (member (caar data) '(350 360)))
       (setq obj (entget (cdar data)))
       (if (= (cdr (assoc 0 obj)) "DICTIONARY")
         (velec:dump (cdar data) (strcat path "/" key))
         (progn
           (princ (strcat "\n" path "/" key))
           (foreach item obj
             (if (member (car item) '(1 2 40 90 290 300 302 303))
               (progn (princ "\n  ") (prin1 item))))))
       (setq key nil)))
    (setq data (cdr data))))

(defun c:VELECDUMP (/ root)
  (if (setq root (dictsearch (namedobjdict) "VELEC"))
    (velec:dump (cdr (assoc -1 root)) "VELEC")
    (princ "\nВ чертеже нет словаря VELEC."))
  (princ))

(princ "\nVELECDUMP — вывести схему из NOD/VELEC.")
(princ)
